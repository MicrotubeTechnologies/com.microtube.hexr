using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace HexR
{
    /// <summary>
    /// Says "a HexR headset is here" on the local network every two seconds, so a phone lists it
    /// without anyone typing an address.
    ///
    /// Send-only UDP broadcast. Sending a broadcast needs no multicast lock or extra permission on
    /// Android; only receiving one does, and that is the phone's job. The phone takes the
    /// headset's address from where the packet came from, which is why nothing here has to ask the
    /// OS for its own IP (Mono's network-interface API is unreliable on recent Android).
    /// Venue Wi-Fi that isolates clients from each other blocks this; the phone can still connect
    /// by typing the headset's IP.
    /// </summary>
    internal sealed class RemoteDiscovery : IDisposable
    {
        private const int IntervalMs = 2000;

        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private UdpClient udp;
        private Thread thread;
        private volatile bool running;
        private volatile byte[] payload;
        private int port;
        private bool warned;

        public void Start(int discoveryPort)
        {
            port = discoveryPort;
            udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "HexRRemoteAnnounce" };
            thread.Start();
        }

        /// <summary>Main thread: what to announce from now on, sent straight away.</summary>
        public void SetAnnounce(string json)
        {
            payload = Encoding.UTF8.GetBytes(json);
            wake.Set();
        }

        public void Dispose()
        {
            running = false;
            wake.Set();
            try
            {
                udp?.Close();
            }
            catch (Exception)
            {
                // Already closed.
            }
        }

        private void Loop()
        {
            IPEndPoint everyone = new IPEndPoint(IPAddress.Broadcast, port);
            while (running)
            {
                byte[] bytes = payload;
                if (bytes != null)
                {
                    try
                    {
                        udp.Send(bytes, bytes.Length, everyone);
                        warned = false;
                    }
                    catch (Exception e)
                    {
                        // Wi-Fi off or reconnecting. Said once per outage, then retried quietly.
                        if (running && !warned)
                        {
                            warned = true;
                            Debug.LogWarning("[HexR] Remote: could not announce on the network (" + e.Message + "). Retrying.");
                        }
                    }
                }

                wake.WaitOne(IntervalMs);
            }
        }
    }
}
