package com.microtube.hexr.remote;

import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioPlaybackCaptureConfiguration;
import android.media.AudioRecord;
import android.media.AudioTimestamp;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.projection.MediaProjection;
import android.os.Process;
import android.util.Log;

import java.io.DataOutputStream;
import java.io.IOException;
import java.nio.ByteBuffer;

/**
 * The app's own sound while the view is shared: Android's AudioPlaybackCapture on the session's
 * MediaProjection token (the one the wearer already allowed), encoded to AAC and served to phones
 * on a port of its own.
 *
 * Only what this app plays (its own UID), as media or game audio: never the microphone, another
 * app or the system's sounds. USAGE_UNKNOWN is left out, as Meta's MediaProjection notes ask, so
 * muting the headset mutes what phones hear too.
 *
 * Each AAC frame is stamped with when it played, in microseconds on CLOCK_MONOTONIC: the clock the
 * video frames and haptics lines carry, so a recording lines the three up without guessing.
 *
 * Android only lets an app with RECORD_AUDIO build this AudioRecord, although nothing here touches
 * the microphone. CaptureService checks for it before starting one.
 */
final class AudioCapture {

    static final int SAMPLE_RATE = 48000;       // Horizon OS and PICO both mix at 48 kHz
    static final int CHANNELS = 2;
    private static final int CODEC_AAC = 1;
    private static final int BITRATE = 128000;
    private static final int FRAMES_PER_READ = 1024;   // one AAC frame's worth
    private static final int BYTES_PER_FRAME = 2 * CHANNELS;
    private static final long INPUT_WAIT_US = 20000;
    // Gaps or a jump in Android's timestamp bigger than this move the clock; anything smaller is
    // jitter, and the stamps carry on sample by sample.
    private static final long RESYNC_US = 40000;

    private static final String TAG = HexRScreenCapture.TAG;
    private static final String MIME = MediaFormat.MIMETYPE_AUDIO_AAC;

    interface Listener {
        /** The sound stopped on its own: the capture or the encoder failed. Not called on stop(). */
        void onFailed(String message);
    }

    private final MediaProjection projection;
    private final Listener listener;
    private AudioRecord record;
    private MediaCodec codec;
    private StreamServer server;
    private Thread thread;
    private volatile boolean running;
    private long baseUs = -1;          // where frame 0 sits on CLOCK_MONOTONIC

    AudioCapture(MediaProjection projection, Listener listener) {
        this.projection = projection;
        this.listener = listener;
    }

    /**
     * Starts capturing and serving the sound. Returns the port it is served on; throws, having
     * let go of everything, if this headset will not capture it.
     */
    int start(int preferredPort) throws Exception {
        try {
            AudioPlaybackCaptureConfiguration config = new AudioPlaybackCaptureConfiguration.Builder(projection)
                    .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                    .addMatchingUsage(AudioAttributes.USAGE_GAME)
                    .addMatchingUid(Process.myUid())
                    .build();
            AudioFormat format = new AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(SAMPLE_RATE)
                    .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
                    .build();
            int minimum = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT);
            record = new AudioRecord.Builder()
                    .setAudioFormat(format)
                    .setBufferSizeInBytes(Math.max(minimum, FRAMES_PER_READ * BYTES_PER_FRAME * 8))
                    .setAudioPlaybackCaptureConfig(config)
                    .build();
            if (record.getState() != AudioRecord.STATE_INITIALIZED) {
                throw new IllegalStateException("the headset would not capture this app's sound");
            }

            codec = MediaCodec.createEncoderByType(MIME);
            MediaFormat aac = MediaFormat.createAudioFormat(MIME, SAMPLE_RATE, CHANNELS);
            aac.setInteger(MediaFormat.KEY_AAC_PROFILE, MediaCodecInfo.CodecProfileLevel.AACObjectLC);
            aac.setInteger(MediaFormat.KEY_BIT_RATE, BITRATE);
            aac.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, FRAMES_PER_READ * BYTES_PER_FRAME * 2);
            codec.configure(aac, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);

            server = new StreamServer("Sound", new StreamServer.Header() {
                @Override
                public void write(DataOutputStream out) throws IOException {
                    out.writeBytes("HXA1");
                    out.writeInt(SAMPLE_RATE);
                    out.writeByte(CHANNELS);
                    out.writeByte(CODEC_AAC);
                }
            }, new StreamServer.Listener() {
                @Override
                public void onKeyframeNeeded() {
                    // Every AAC frame decodes on its own.
                }

                @Override
                public void onViewerCountChanged(int count) {
                }
            });
            int port = server.start(preferredPort);

            codec.start();
            record.startRecording();
            running = true;
            thread = new Thread(new Runnable() {
                @Override
                public void run() {
                    loop();
                }
            }, "HexRRemoteSound");
            thread.start();
            Log.i(TAG, "Sharing the sound, AAC " + SAMPLE_RATE + " Hz, on port " + port);
            return port;
        } catch (Exception e) {
            release();
            throw e;
        }
    }

    /** Ends the sound, if it is running. Safe to call more than once and from any thread. */
    void stop() {
        running = false;
        AudioRecord r = record;
        if (r != null) {
            try {
                r.stop();          // wakes the read the thread is blocked in
            } catch (Throwable t) {
                Log.w(TAG, "record.stop", t);
            }
        }

        Thread t = thread;
        if (t != null && t != Thread.currentThread()) {
            try {
                t.join(1000);
            } catch (InterruptedException ignored) {
                Thread.currentThread().interrupt();
            }
        }

        release();
    }

    private void loop() {
        byte[] pcm = new byte[FRAMES_PER_READ * BYTES_PER_FRAME];
        AudioTimestamp stamp = new AudioTimestamp();
        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        long frames = 0;
        try {
            while (running) {
                int n = record.read(pcm, 0, pcm.length);
                if (n < 0) {
                    throw new IllegalStateException("reading the sound failed (" + n + ")");
                }

                int count = n / BYTES_PER_FRAME;
                if (count == 0) {
                    continue;
                }

                feed(pcm, count * BYTES_PER_FRAME, playedAt(frames, count, stamp), info);
                frames += count;
                drain(info);
            }
        } catch (Throwable t) {
            if (running) {
                Log.e(TAG, "The sound stopped", t);
                running = false;
                listener.onFailed("Sharing the sound stopped: " + t.getMessage());
            }
        }
    }

    /**
     * When the first of these `count` frames played, in µs on CLOCK_MONOTONIC. Android's
     * timestamp says when one frame was captured; the rest follow sample by sample, so stamps do
     * not wobble with when each read happens to return.
     */
    private long playedAt(long first, int count, AudioTimestamp stamp) {
        long measured;
        if (record.getTimestamp(stamp, AudioTimestamp.TIMEBASE_MONOTONIC) == AudioRecord.SUCCESS) {
            measured = (stamp.nanoTime + (first - stamp.framePosition) * 1000000000L / SAMPLE_RATE) / 1000;
        } else {
            // No timestamp yet: these frames have just been read, so they ended about now.
            measured = System.nanoTime() / 1000 - (long) count * 1000000L / SAMPLE_RATE;
        }

        long offset = first * 1000000L / SAMPLE_RATE;
        if (baseUs < 0 || Math.abs(measured - (baseUs + offset)) > RESYNC_US) {
            baseUs = measured - offset;
        }

        return baseUs + offset;
    }

    /** Hands PCM to the encoder, in as many input buffers as it takes. */
    private void feed(byte[] pcm, int length, long ptsUs, MediaCodec.BufferInfo info) {
        int offset = 0;
        while (offset < length && running) {
            int index = codec.dequeueInputBuffer(INPUT_WAIT_US);
            if (index < 0) {
                drain(info);       // the encoder is full: take what it has made first
                continue;
            }

            ByteBuffer in = codec.getInputBuffer(index);
            in.clear();
            int n = Math.min(in.remaining(), length - offset);
            in.put(pcm, offset, n);
            long t = ptsUs + (long) (offset / BYTES_PER_FRAME) * 1000000L / SAMPLE_RATE;
            codec.queueInputBuffer(index, 0, n, t, 0);
            offset += n;
        }
    }

    /** Sends every AAC frame the encoder has ready. The first is its AudioSpecificConfig. */
    private void drain(MediaCodec.BufferInfo info) {
        while (running) {
            int index = codec.dequeueOutputBuffer(info, 0);
            if (index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                continue;
            }

            if (index < 0) {
                return;
            }

            ByteBuffer out = codec.getOutputBuffer(index);
            if (out != null && info.size > 0) {
                byte[] data = new byte[info.size];
                out.position(info.offset);
                out.limit(info.offset + info.size);
                out.get(data);
                if ((info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) {
                    server.publish(StreamServer.FLAG_CONFIG, 0, data);
                } else {
                    server.publish(StreamServer.FLAG_KEY, info.presentationTimeUs, data);
                }
            }

            codec.releaseOutputBuffer(index, false);
        }
    }

    private synchronized void release() {
        if (record != null) {
            try {
                record.release();
            } catch (Throwable t) {
                Log.w(TAG, "record.release", t);
            }

            record = null;
        }

        if (codec != null) {
            try {
                codec.stop();
            } catch (Throwable t) {
                // Never started, or already stopped.
            }

            try {
                codec.release();
            } catch (Throwable t) {
                Log.w(TAG, "codec.release", t);
            }

            codec = null;
        }

        if (server != null) {
            server.close();
            server = null;
        }
    }
}
