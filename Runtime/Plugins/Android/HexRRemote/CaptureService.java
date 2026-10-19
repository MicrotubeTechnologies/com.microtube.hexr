package com.microtube.hexr.remote;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.ComponentName;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ServiceInfo;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.SystemClock;
import android.util.Log;
import android.view.Surface;

import java.io.DataOutputStream;
import java.io.IOException;
import java.nio.ByteBuffer;

/**
 * Runs one capture session: holds the MediaProjection, feeds it into an H.264 encoder and hands
 * every encoded packet to the video's StreamServer. When the app shares its sound, an
 * AudioCapture on the same token serves that beside it.
 *
 * It has to be a foreground service of type mediaProjection: from Android 14 that is the only
 * place getMediaProjection may be called, and only after startForeground. The order below is the
 * one Android enforces, and getting it wrong throws inside the Unity app's own process, so every
 * entry point here catches Throwable rather than let a capture problem take the app down.
 */
public final class CaptureService extends Service {

    private static final String TAG = HexRScreenCapture.TAG;
    private static final int NOTIFICATION_ID = 0x4852;
    private static final String CHANNEL_ID = "hexr_remote_capture";
    private static final String MIME = MediaFormat.MIMETYPE_VIDEO_AVC;
    private static final int CODEC_H264 = 1;
    private static final long SYNC_FRAME_GAP_MS = 500;

    private static volatile CaptureService current;

    private HandlerThread codecThread;
    private Handler codecHandler;
    private MediaProjection projection;
    private MediaProjection.Callback projectionCallback;
    private MediaCodec codec;
    private Surface inputSurface;
    private VirtualDisplay display;
    private StreamServer server;
    private AudioCapture sound;
    private volatile int videoWidth;
    private volatile int videoHeight;
    private boolean started;
    private boolean stopped;
    private long lastSyncRequest;

    static CaptureService current() {
        return current;
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        // A restarted sticky service gets a null intent; this one is not sticky, but a null here
        // still means there is no consent to act on.
        if (intent == null || started) {
            if (!started) {
                stopSelf();
            }

            return START_NOT_STICKY;
        }

        started = true;
        current = this;

        // First, before anything else can fail: a service started with startForegroundService
        // that never calls startForeground is killed with the app a few seconds later.
        try {
            startForegroundWithNotice();
        } catch (Throwable t) {
            Log.e(TAG, "startForeground failed", t);
            finish(HexRScreenCapture.STATE_ERROR, "This headset would not allow a capture service: " + t.getMessage());
            return START_NOT_STICKY;
        }

        Intent consent = HexRScreenCapture.takePendingData();
        if (consent == null) {
            finish(HexRScreenCapture.STATE_ERROR, "The capture permission was missing.");
            return START_NOT_STICKY;
        }

        try {
            begin(HexRScreenCapture.pendingResultCode(), consent);
        } catch (Throwable t) {
            Log.e(TAG, "Capture failed to start", t);
            finish(HexRScreenCapture.STATE_ERROR, "Capture failed to start: " + t.getMessage());
        }

        return START_NOT_STICKY;
    }

    /**
     * Android calls this for every task of the package that is removed, not just the app's. The
     * consent prompt runs in a task of its own on Quest, and that task closes the moment the
     * participant answers, so it must not end the capture it has just allowed. Any other task
     * going means the app was closed.
     */
    @Override
    public void onTaskRemoved(Intent rootIntent) {
        ComponentName root = rootIntent != null ? rootIntent.getComponent() : null;
        if (root == null || !ProjectionRequestActivity.class.getName().equals(root.getClassName())) {
            finish(HexRScreenCapture.STATE_OFF, "");
        }

        super.onTaskRemoved(rootIntent);
    }

    @Override
    public void onDestroy() {
        finish(HexRScreenCapture.STATE_OFF, "");
        super.onDestroy();
    }

    /** Ends the session from any thread. */
    void requestStop(final String state, final String message) {
        Handler handler = codecHandler;
        if (handler != null) {
            handler.post(new Runnable() {
                @Override
                public void run() {
                    finish(state, message);
                }
            });
        } else {
            finish(state, message);
        }
    }

    /** A phone has joined and needs a keyframe to start decoding from. */
    void requestSyncFrame() {
        Handler handler = codecHandler;
        if (handler == null) {
            return;
        }

        handler.post(new Runnable() {
            @Override
            public void run() {
                long now = SystemClock.elapsedRealtime();
                if (codec == null || now - lastSyncRequest < SYNC_FRAME_GAP_MS) {
                    return;
                }

                lastSyncRequest = now;
                try {
                    Bundle params = new Bundle();
                    params.putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0);
                    codec.setParameters(params);
                } catch (Throwable t) {
                    Log.w(TAG, "Sync frame request failed", t);
                }
            }
        });
    }

    private void startForegroundWithNotice() {
        NotificationManager notifications = getSystemService(NotificationManager.class);
        NotificationChannel channel = new NotificationChannel(CHANNEL_ID, "Screen sharing", NotificationManager.IMPORTANCE_LOW);
        channel.setDescription("Shown while a HexR Remote phone can see the headset view.");
        notifications.createNotificationChannel(channel);

        // A framework icon: the app's own launcher icon can be an adaptive icon, which a
        // notification refuses, and a bad notification is another way to crash here. Without
        // POST_NOTIFICATIONS (which Meta reviews) the notice is simply not shown; the service
        // still runs.
        Notification notice = new Notification.Builder(this, CHANNEL_ID)
                .setSmallIcon(android.R.drawable.presence_video_online)
                .setContentTitle("Sharing the headset view")
                .setContentText("A phone running HexR Remote can see what you see.")
                .setOngoing(true)
                .build();

        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(NOTIFICATION_ID, notice, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
        } else {
            startForeground(NOTIFICATION_ID, notice);
        }
    }

    private void begin(int resultCode, Intent consent) throws Exception {
        codecThread = new HandlerThread("HexRRemoteCodec");
        codecThread.start();
        codecHandler = new Handler(codecThread.getLooper());

        MediaProjectionManager manager = getSystemService(MediaProjectionManager.class);
        projection = manager.getMediaProjection(resultCode, consent);
        if (projection == null) {
            throw new IllegalStateException("the headset returned no capture token");
        }

        // Required before createVirtualDisplay from Android 14, and Meta requires it on Quest:
        // another app taking the capture ends ours, and this is how we hear about it.
        projectionCallback = new MediaProjection.Callback() {
            @Override
            public void onStop() {
                finish(HexRScreenCapture.STATE_OFF, "The headset ended the screen sharing.");
            }
        };
        projection.registerCallback(projectionCallback, codecHandler);

        int width = HexRScreenCapture.requestedWidth;
        int height = HexRScreenCapture.requestedHeight;
        int fps = HexRScreenCapture.requestedFps;

        videoWidth = width;
        videoHeight = height;
        server = new StreamServer("Video", new StreamServer.Header() {
            @Override
            public void write(DataOutputStream out) throws IOException {
                out.writeBytes("HXV1");
                out.writeShort(videoWidth);
                out.writeShort(videoHeight);
                out.writeByte(CODEC_H264);
            }
        }, new StreamServer.Listener() {
            @Override
            public void onKeyframeNeeded() {
                requestSyncFrame();
            }

            @Override
            public void onViewerCountChanged(int count) {
                HexRScreenCapture.setViewers(count);
            }
        });
        int port = server.start(HexRScreenCapture.requestedPort);
        HexRScreenCapture.setPort(port);
        HexRScreenCapture.setSize(width, height);

        codec = createEncoder(width, height, HexRScreenCapture.requestedBitrate, fps);
        inputSurface = codec.createInputSurface();
        codec.start();

        int dpi = getResources().getDisplayMetrics().densityDpi;
        // On Quest the compositor fills this surface itself and does its own scaling; the
        // VirtualDisplay is little more than the handle that starts it (Meta's MediaProjection
        // notes). Created exactly once: a second call on the same token throws.
        display = projection.createVirtualDisplay("HexRRemote", width, height, dpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, inputSurface, null, codecHandler);

        HexRScreenCapture.setState(HexRScreenCapture.STATE_LIVE, "");
        Log.i(TAG, "Capturing " + width + "x" + height + " at " + fps + " fps on port " + port);
        startSound();
    }

    /**
     * The app's sound beside the view, when the app shares it. Whatever goes wrong here costs the
     * sound and says why in the status; the view carries on.
     */
    private void startSound() {
        if (!HexRScreenCapture.requestedSound) {
            HexRScreenCapture.setSound(HexRScreenCapture.STATE_OFF, "", 0);
            return;
        }

        if (Build.VERSION.SDK_INT < 29) {
            HexRScreenCapture.setSound(HexRScreenCapture.STATE_UNSUPPORTED, "This headset cannot share its sound: that needs Android 10.", 0);
            return;
        }

        if (!HexRScreenCapture.declaresSoundPermission(this)) {
            HexRScreenCapture.setSound(HexRScreenCapture.STATE_UNSUPPORTED,
                    "This build of the app cannot share its sound: it was made without the RECORD_AUDIO permission.", 0);
            return;
        }

        if (checkSelfPermission(HexRScreenCapture.SOUND_PERMISSION) != PackageManager.PERMISSION_GRANTED) {
            HexRScreenCapture.setSound(HexRScreenCapture.STATE_DENIED,
                    "The participant did not allow sharing the sound. It can be allowed in the app's permissions, under Microphone.", 0);
            return;
        }

        try {
            sound = new AudioCapture(projection, new AudioCapture.Listener() {
                @Override
                public void onFailed(final String message) {
                    Handler handler = codecHandler;
                    if (handler != null) {
                        handler.post(new Runnable() {
                            @Override
                            public void run() {
                                stopSound(HexRScreenCapture.STATE_ERROR, message);
                            }
                        });
                    }
                }
            });
            int port = sound.start(HexRScreenCapture.requestedSoundPort);
            HexRScreenCapture.setSound(HexRScreenCapture.STATE_LIVE, "", port);
        } catch (Throwable t) {
            Log.e(TAG, "The sound could not be shared", t);
            sound = null;
            HexRScreenCapture.setSound(HexRScreenCapture.STATE_ERROR, "Sharing the sound failed: " + t.getMessage(), 0);
        }
    }

    private void stopSound(String state, String message) {
        if (sound != null) {
            sound.stop();
            sound = null;
        }

        HexRScreenCapture.setSound(state, message, 0);
    }

    /**
     * Low-latency settings for a phone on the same Wi-Fi. Every optional key is one an encoder may
     * not support; if configure rejects the full set, it is retried with only the required keys
     * rather than give up on capture over a tuning hint.
     */
    private MediaCodec createEncoder(int width, int height, int bitrate, int fps) throws Exception {
        try {
            return configureEncoder(width, height, bitrate, fps, true);
        } catch (Exception e) {
            Log.w(TAG, "Encoder rejected the tuned settings, retrying with the basics", e);
            return configureEncoder(width, height, bitrate, fps, false);
        }
    }

    private MediaCodec configureEncoder(int width, int height, int bitrate, int fps, boolean tuned) throws Exception {
        MediaCodec encoder = MediaCodec.createEncoderByType(MIME);
        try {
            MediaFormat format = MediaFormat.createVideoFormat(MIME, width, height);
            format.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
            format.setInteger(MediaFormat.KEY_BIT_RATE, bitrate);
            format.setInteger(MediaFormat.KEY_FRAME_RATE, fps);
            format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1);

            if (tuned) {
                MediaCodecInfo.EncoderCapabilities caps =
                        encoder.getCodecInfo().getCapabilitiesForType(MIME).getEncoderCapabilities();
                if (caps.isBitrateModeSupported(MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR)) {
                    format.setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR);
                }

                // Baseline has no B-frames, so nothing waits on a later frame before it can show.
                // No level: 1024x1024 is past Level 3.1, and the encoder picks one that fits.
                format.setInteger(MediaFormat.KEY_PROFILE, MediaCodecInfo.CodecProfileLevel.AVCProfileBaseline);
                format.setInteger(MediaFormat.KEY_PRIORITY, 0);
                format.setInteger(MediaFormat.KEY_OPERATING_RATE, fps);
                // A headset view that stops changing still sends frames, so a phone that joins
                // during a still moment is not left on a blank screen.
                format.setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 100000L);
                if (Build.VERSION.SDK_INT >= 29) {
                    // The compositor may push frames at the display rate (72 to 120 Hz).
                    format.setFloat(MediaFormat.KEY_MAX_FPS_TO_ENCODER, fps);
                    // Every keyframe carries SPS/PPS, so a phone can start from any of them.
                    format.setInteger(MediaFormat.KEY_PREPEND_HEADER_TO_SYNC_FRAMES, 1);
                }

                if (Build.VERSION.SDK_INT >= 30) {
                    format.setInteger(MediaFormat.KEY_LATENCY, 1);
                }
            }

            // Async mode: the callback has to be set before configure.
            encoder.setCallback(new EncoderCallback(), codecHandler);
            encoder.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            return encoder;
        } catch (Exception e) {
            encoder.release();
            throw e;
        }
    }

    private final class EncoderCallback extends MediaCodec.Callback {
        @Override
        public void onInputBufferAvailable(MediaCodec mc, int index) {
            // Surface input: frames arrive from the compositor, not from buffers.
        }

        @Override
        public void onOutputBufferAvailable(MediaCodec mc, int index, MediaCodec.BufferInfo info) {
            try {
                ByteBuffer buffer = mc.getOutputBuffer(index);
                StreamServer out = server;
                if (buffer != null && info.size > 0 && out != null) {
                    byte[] data = new byte[info.size];
                    buffer.position(info.offset);
                    buffer.limit(info.offset + info.size);
                    buffer.get(data);

                    int flags = 0;
                    if ((info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) {
                        flags |= StreamServer.FLAG_CONFIG;
                    }

                    if ((info.flags & MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0) {
                        flags |= StreamServer.FLAG_KEY;
                    }

                    out.publish(flags, info.presentationTimeUs, data);
                }

                mc.releaseOutputBuffer(index, false);
            } catch (Throwable t) {
                Log.w(TAG, "Dropped an encoded frame", t);
            }
        }

        @Override
        public void onError(MediaCodec mc, MediaCodec.CodecException e) {
            Log.e(TAG, "Encoder error", e);
            finish(HexRScreenCapture.STATE_ERROR, "The video encoder failed: " + e.getDiagnosticInfo());
        }

        @Override
        public void onOutputFormatChanged(MediaCodec mc, MediaFormat format) {
            try {
                int w = format.getInteger(MediaFormat.KEY_WIDTH);
                int h = format.getInteger(MediaFormat.KEY_HEIGHT);
                HexRScreenCapture.setSize(w, h);
                videoWidth = w;
                videoHeight = h;
            } catch (Throwable t) {
                Log.w(TAG, "Could not read the encoder's output size", t);
            }
        }
    }

    /**
     * Tears the session down in the order that releases cleanly: the sound stops, the display
     * stops feeding the encoder, the encoder stops, then the token is given back. Safe to call
     * more than once and from any thread; stopping the projection fires onStop, which lands back
     * here.
     */
    private synchronized void finish(String state, String message) {
        if (stopped) {
            return;
        }

        stopped = true;
        if (current == this) {
            current = null;
        }

        stopSound(HexRScreenCapture.STATE_OFF, "");

        if (display != null) {
            try {
                display.release();
            } catch (Throwable t) {
                Log.w(TAG, "display.release", t);
            }

            display = null;
        }

        if (codec != null) {
            try {
                codec.stop();
            } catch (Throwable t) {
                Log.w(TAG, "codec.stop", t);
            }

            try {
                codec.release();
            } catch (Throwable t) {
                Log.w(TAG, "codec.release", t);
            }

            codec = null;
        }

        if (inputSurface != null) {
            try {
                inputSurface.release();
            } catch (Throwable t) {
                Log.w(TAG, "surface.release", t);
            }

            inputSurface = null;
        }

        if (projection != null) {
            try {
                if (projectionCallback != null) {
                    projection.unregisterCallback(projectionCallback);
                }

                projection.stop();
            } catch (Throwable t) {
                Log.w(TAG, "projection.stop", t);
            }

            projection = null;
        }

        if (server != null) {
            server.close();
            server = null;
        }

        HexRScreenCapture.setPort(0);
        HexRScreenCapture.setState(state, message);

        try {
            stopForeground(STOP_FOREGROUND_REMOVE);
        } catch (Throwable t) {
            Log.w(TAG, "stopForeground", t);
        }

        stopSelf();

        if (codecThread != null) {
            codecThread.quitSafely();
            codecThread = null;
            codecHandler = null;
        }
    }
}
