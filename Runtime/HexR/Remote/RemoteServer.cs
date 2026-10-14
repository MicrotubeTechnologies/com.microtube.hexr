using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace HexR
{
    /// <summary>A request from a phone, waiting for Unity's main thread.</summary>
    internal sealed class RemoteInbound
    {
        public RemoteClient Client;
        public RemoteRequest Request;
        public DateTime ReceivedUtc;
    }

    /// <summary>
    /// The control connection: phones connect over TCP and exchange one JSON object per line.
    ///
    /// Sockets live on background threads and never touch Unity: requests are queued for
    /// HexRRemote.Update to run on the main thread, and what goes back is either a reply queued by
    /// the main thread or the latest status line it published. Only the newest status is kept per
    /// phone, so a slow phone gets fewer updates rather than a growing backlog.
    /// </summary>
    internal sealed class RemoteServer : IDisposable
    {
        private const int PortAttempts = 10;

        private readonly ConcurrentQueue<RemoteInbound> inbox = new ConcurrentQueue<RemoteInbound>();
        private readonly List<RemoteClient> clients = new List<RemoteClient>();
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;
        private volatile string welcomeLine = "";
        private volatile string statusLine = "";

        /// <summary>
        /// Set while the app is paused (headset off or asleep). Update does not run then, so a
        /// queued request would sit there and run the moment the headset goes back on: a scene
        /// change or a capture prompt arriving in front of whoever puts it on next. Refused instead.
        /// </summary>
        public volatile bool Paused;

        public int Port { get; private set; }

        public int ClientCount
        {
            get
            {
                lock (clients)
                {
                    return clients.Count;
                }
            }
        }

        internal string StatusLine => statusLine;

        /// <summary>
        /// Listens on the preferred port, or the next free one, or any free one. A HexR app that has
        /// not quite let go of the port must not leave this one unreachable; the real port is in
        /// every announce, so phones never assume it.
        /// </summary>
        public void Start(int preferredPort)
        {
            SocketException last = null;
            for (int i = 0; i <= PortAttempts && listener == null; i++)
            {
                int port = i == PortAttempts ? 0 : preferredPort + i;
                TcpListener candidate = new TcpListener(IPAddress.Any, port);
                try
                {
#if UNITY_ANDROID && !UNITY_EDITOR
                    // Lets a restarted app take its port back from a socket still in TIME_WAIT. Not
                    // on Windows, where the same option lets two listeners share a port.
                    candidate.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
#endif
                    candidate.Start();
                    listener = candidate;
                }
                catch (SocketException e)
                {
                    last = e;
                }
            }

            if (listener == null)
            {
                throw last ?? new SocketException((int)SocketError.AddressAlreadyInUse);
            }

            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            running = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "HexRRemoteAccept" };
            acceptThread.Start();
        }

        public void SetWelcome(string line)
        {
            welcomeLine = line;
        }

        /// <summary>Main thread: the newest status, sent to every phone at its next chance.</summary>
        public void PublishStatus(string line)
        {
            statusLine = line;
            foreach (RemoteClient client in Snapshot())
            {
                client.Wake();
            }
        }

        public bool TryTake(out RemoteInbound item)
        {
            return inbox.TryDequeue(out item);
        }

        public void Dispose()
        {
            running = false;
            try
            {
                listener?.Stop();
            }
            catch (Exception)
            {
                // Already closed.
            }

            foreach (RemoteClient client in Snapshot())
            {
                client.Close();
            }

            lock (clients)
            {
                clients.Clear();
            }
        }

        // ---- called from the clients' threads ---------------------------------------------------

        internal void OnLine(RemoteClient client, string line)
        {
            if (!RemoteProtocol.TryParse(line, out RemoteRequest request, out string error))
            {
                client.Send(RemoteProtocol.ErrorLine("", error));
                return;
            }

            switch (request.type)
            {
                case RemoteProtocol.Ping:
                    // Only keeps the connection alive; the status stream is the reply.
                    return;

                case RemoteProtocol.Hello:
                    client.Send(welcomeLine);
                    return;
            }

            if (Paused)
            {
                client.Send(RemoteProtocol.ErrorLine(request.type,
                    "The headset is asleep or not being worn. Try again once it is in use."));
                return;
            }

            inbox.Enqueue(new RemoteInbound { Client = client, Request = request, ReceivedUtc = DateTime.UtcNow });
        }

        internal void OnClosed(RemoteClient client)
        {
            lock (clients)
            {
                clients.Remove(client);
            }

            Debug.Log("[HexR] Remote: phone at " + client.Address + " disconnected.");
        }

        private void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    TcpClient tcp = listener.AcceptTcpClient();
                    RemoteClient client = new RemoteClient(this, tcp);
                    lock (clients)
                    {
                        clients.Add(client);
                    }

                    client.Start();
                    Debug.Log("[HexR] Remote: phone connected from " + client.Address + ".");
                }
                catch (Exception e)
                {
                    if (running)
                    {
                        Debug.LogWarning("[HexR] Remote: accepting a phone failed: " + e.Message);
                        Thread.Sleep(200);
                    }
                }
            }
        }

        private RemoteClient[] Snapshot()
        {
            lock (clients)
            {
                return clients.ToArray();
            }
        }
    }

    /// <summary>One phone's control connection: a reader thread and a writer thread.</summary>
    internal sealed class RemoteClient
    {
        // A phone pings every few seconds; one silent this long has gone (out of Wi-Fi, app
        // killed) without closing the socket, and would otherwise count as connected forever.
        private const int IdleTimeoutMs = 15000;

        // How often the status is repeated when nothing new was published, so a phone can tell a
        // paused headset (statuses still arriving, "paused": true) from a vanished one.
        private const int HeartbeatMs = 1000;

        private const int WriteTimeoutMs = 5000;

        private readonly RemoteServer server;
        private readonly TcpClient tcp;
        private readonly ConcurrentQueue<string> outbox = new ConcurrentQueue<string>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private NetworkStream stream;
        private volatile bool open = true;
        private int closed;

        public readonly string Address;

        public RemoteClient(RemoteServer server, TcpClient tcp)
        {
            this.server = server;
            this.tcp = tcp;
            Address = tcp.Client.RemoteEndPoint is IPEndPoint ep ? ep.Address.ToString() : "?";
        }

        public void Start()
        {
            tcp.NoDelay = true;
            stream = tcp.GetStream();
            stream.ReadTimeout = IdleTimeoutMs;
            stream.WriteTimeout = WriteTimeoutMs;
            new Thread(ReadLoop) { IsBackground = true, Name = "HexRRemoteRead" }.Start();
            new Thread(WriteLoop) { IsBackground = true, Name = "HexRRemoteWrite" }.Start();
        }

        /// <summary>Any thread: queue one line for this phone.</summary>
        public void Send(string line)
        {
            if (!open || string.IsNullOrEmpty(line))
            {
                return;
            }

            outbox.Enqueue(line);
            wake.Set();
        }

        public void Wake()
        {
            wake.Set();
        }

        public void Close()
        {
            // The reader and the writer both end here; only the first one closes.
            if (Interlocked.Exchange(ref closed, 1) == 1)
            {
                return;
            }

            open = false;
            wake.Set();
            try
            {
                tcp.Close();
            }
            catch (Exception)
            {
                // Already closed.
            }

            server.OnClosed(this);
        }

        private void ReadLoop()
        {
            byte[] buffer = new byte[4096];
            MemoryStream line = new MemoryStream();
            try
            {
                while (open)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    for (int i = 0; i < read; i++)
                    {
                        byte b = buffer[i];
                        if (b == (byte)'\n')
                        {
                            string text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
                            line.SetLength(0);
                            if (text.Length > 0)
                            {
                                server.OnLine(this, text);
                            }
                        }
                        else
                        {
                            line.WriteByte(b);
                            if (line.Length > RemoteProtocol.MaxLineBytes)
                            {
                                // Not a HexR phone, or something badly wrong with one.
                                Close();
                                return;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Timed out with no ping, or the phone went away.
            }

            Close();
        }

        private void WriteLoop()
        {
            string sentStatus = null;
            DateTime lastSend = DateTime.MinValue;
            try
            {
                while (open)
                {
                    wake.WaitOne(HeartbeatMs);
                    if (!open)
                    {
                        break;
                    }

                    while (outbox.TryDequeue(out string reply))
                    {
                        Write(reply);
                        lastSend = DateTime.UtcNow;
                    }

                    // Reference comparison on purpose: a new status object is published every tick,
                    // and the heartbeat covers the case where none was.
                    string status = server.StatusLine;
                    bool fresh = !ReferenceEquals(status, sentStatus);
                    bool heartbeat = (DateTime.UtcNow - lastSend).TotalMilliseconds >= HeartbeatMs;
                    if (!string.IsNullOrEmpty(status) && (fresh || heartbeat))
                    {
                        Write(status);
                        sentStatus = status;
                        lastSend = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception)
            {
                // The phone went away or stopped reading.
            }

            Close();
        }

        private void Write(string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
