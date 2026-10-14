package com.microtube.hexr.remote;

import android.util.Log;

import java.io.BufferedOutputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.TimeUnit;

/**
 * Serves the encoded headset view to any number of phones over plain TCP.
 *
 * Wire format (Documentation~/RemoteProtocol.md, "Video"), all integers big-endian:
 *   once per connection:  "HXV1"  u16 width  u16 height  u8 codec (1 = H.264)
 *   then per packet:      u32 payloadLength  u8 flags (1 config, 2 key)  u64 ptsMicros  payload
 * The payload is the encoder's own Annex-B output, unchanged.
 *
 * A phone that falls behind is not allowed to fall further: its queue is bounded, and when it
 * fills, the backlog is thrown away and the phone waits for the next keyframe. Old video is
 * worth nothing to someone watching live.
 */
final class VideoServer {

    static final int FLAG_CONFIG = 1;
    static final int FLAG_KEY = 2;

    private static final String TAG = HexRScreenCapture.TAG;
    private static final int CODEC_H264 = 1;
    private static final int QUEUE_PACKETS = 90;
    private static final int PORT_ATTEMPTS = 10;

    interface Listener {
        void onKeyframeNeeded();

        void onViewerCountChanged(int count);
    }

    private final Listener listener;
    private final CopyOnWriteArrayList<Viewer> viewers = new CopyOnWriteArrayList<>();
    private ServerSocket serverSocket;
    private volatile boolean running;
    private volatile byte[] config;
    private volatile int width;
    private volatile int height;

    VideoServer(Listener listener) {
        this.listener = listener;
    }

    void setSize(int w, int h) {
        width = w;
        height = h;
    }

    /**
     * Listens on the preferred port, or the next free one after it, or any free port. Another
     * HexR app that has not quite let go of the port must not stop this one streaming; the real
     * port goes out in every status, so phones never assume it.
     */
    int start(int preferredPort) throws IOException {
        IOException last = null;
        for (int i = 0; i <= PORT_ATTEMPTS; i++) {
            int port = i == PORT_ATTEMPTS ? 0 : preferredPort + i;
            ServerSocket socket = new ServerSocket();
            try {
                socket.setReuseAddress(true);
                socket.bind(new InetSocketAddress(port));
                serverSocket = socket;
                break;
            } catch (IOException e) {
                last = e;
                socket.close();
            }
        }

        if (serverSocket == null) {
            throw last != null ? last : new IOException("no port to listen on");
        }

        running = true;
        Thread accept = new Thread(new Runnable() {
            @Override
            public void run() {
                acceptLoop();
            }
        }, "HexRRemoteVideoAccept");
        accept.setDaemon(true);
        accept.start();
        return serverSocket.getLocalPort();
    }

    void publish(int flags, long ptsUs, byte[] data) {
        Packet packet = new Packet(flags, ptsUs, data);
        if ((flags & FLAG_CONFIG) != 0) {
            config = data;
        }

        for (Viewer viewer : viewers) {
            viewer.offer(packet);
        }
    }

    void close() {
        running = false;
        try {
            if (serverSocket != null) {
                serverSocket.close();
            }
        } catch (IOException ignored) {
        }

        for (Viewer viewer : viewers) {
            viewer.close();
        }

        viewers.clear();
        listener.onViewerCountChanged(0);
    }

    private void acceptLoop() {
        while (running) {
            try {
                Socket socket = serverSocket.accept();
                socket.setTcpNoDelay(true);
                socket.setKeepAlive(true);
                // Small, so a stalled phone backs up into our queue (which drops to a keyframe)
                // rather than into a large kernel buffer full of stale frames.
                socket.setSendBufferSize(64 * 1024);
                Viewer viewer = new Viewer(socket);
                viewers.add(viewer);
                viewer.start();
                listener.onViewerCountChanged(viewers.size());
                listener.onKeyframeNeeded();
                Log.i(TAG, "Phone watching from " + socket.getInetAddress().getHostAddress() + " (" + viewers.size() + " watching)");
            } catch (IOException e) {
                if (running) {
                    Log.w(TAG, "Video accept failed", e);
                }
            } catch (Throwable t) {
                Log.e(TAG, "Video accept loop", t);
            }
        }
    }

    private void remove(Viewer viewer) {
        if (viewers.remove(viewer)) {
            listener.onViewerCountChanged(viewers.size());
            Log.i(TAG, "Phone stopped watching (" + viewers.size() + " watching)");
        }
    }

    private static final class Packet {
        final int flags;
        final long ptsUs;
        final byte[] data;

        Packet(int flags, long ptsUs, byte[] data) {
            this.flags = flags;
            this.ptsUs = ptsUs;
            this.data = data;
        }
    }

    private final class Viewer {
        private final Socket socket;
        private final ArrayBlockingQueue<Packet> queue = new ArrayBlockingQueue<>(QUEUE_PACKETS);
        // A decoder cannot start mid-GOP: nothing is sent until a keyframe arrives.
        private volatile boolean waitingForKey = true;
        private volatile boolean open = true;

        Viewer(Socket socket) {
            this.socket = socket;
        }

        void start() {
            Thread writer = new Thread(new Runnable() {
                @Override
                public void run() {
                    writeLoop();
                }
            }, "HexRRemoteVideoWriter");
            writer.setDaemon(true);
            writer.start();
        }

        void offer(Packet packet) {
            if (!open) {
                return;
            }

            boolean isConfig = (packet.flags & FLAG_CONFIG) != 0;
            boolean isKey = (packet.flags & FLAG_KEY) != 0;
            if (waitingForKey && !isConfig && !isKey) {
                return;
            }

            if (isKey) {
                waitingForKey = false;
            }

            if (!queue.offer(packet)) {
                queue.clear();
                waitingForKey = true;
                listener.onKeyframeNeeded();
            }
        }

        void close() {
            open = false;
            try {
                socket.close();
            } catch (IOException ignored) {
            }
        }

        private void writeLoop() {
            try {
                DataOutputStream out = new DataOutputStream(new BufferedOutputStream(socket.getOutputStream(), 64 * 1024));
                out.writeBytes("HXV1");
                out.writeShort(width);
                out.writeShort(height);
                out.writeByte(CODEC_H264);
                byte[] cached = config;
                if (cached != null) {
                    writePacket(out, new Packet(FLAG_CONFIG, 0, cached));
                }

                out.flush();
                while (open && running) {
                    Packet packet = queue.poll(1, TimeUnit.SECONDS);
                    if (packet == null) {
                        continue;
                    }

                    writePacket(out, packet);
                    if (queue.isEmpty()) {
                        out.flush();
                    }
                }
            } catch (IOException | InterruptedException e) {
                // The phone went away, or the session ended.
            } catch (Throwable t) {
                Log.w(TAG, "Video writer", t);
            } finally {
                close();
                remove(this);
            }
        }

        private void writePacket(DataOutputStream out, Packet packet) throws IOException {
            out.writeInt(packet.data.length);
            out.writeByte(packet.flags);
            out.writeLong(packet.ptsUs);
            out.write(packet.data);
        }
    }
}
