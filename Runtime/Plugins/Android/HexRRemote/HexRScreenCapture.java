package com.microtube.hexr.remote;

import android.app.Activity;
import android.app.Application;
import android.content.Intent;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;

/**
 * The C# side's one door into screen capture (HexRRemote's RemoteStream calls these statics).
 *
 * A capture session is: the participant accepts the system prompt once, a foreground service
 * starts, and the headset view is encoded to H.264 and served to any number of phones on the
 * video port until the session is stopped. Phones come and go without a new prompt; a new
 * session always needs a new one, because Android hands out a MediaProjection token exactly once.
 *
 * Everything here is framework API only. Four of the HexR apps start on UnityPlayerActivity and
 * have no androidx on their classpath, so a single androidx import would fail their build.
 */
public final class HexRScreenCapture {

    static final String TAG = "HexRRemote";

    public static final String STATE_OFF = "off";
    public static final String STATE_REQUESTING = "requesting";
    public static final String STATE_LIVE = "live";
    public static final String STATE_DENIED = "denied";
    public static final String STATE_ERROR = "error";

    private static volatile String state = STATE_OFF;
    private static volatile String message = "";
    private static volatile int width;
    private static volatile int height;
    private static volatile int port;
    private static volatile int viewers;

    // What the session being asked for should look like, read by CaptureService when it starts.
    static volatile int requestedWidth = 1024;
    static volatile int requestedHeight = 1024;
    static volatile int requestedBitrate = 4000000;
    static volatile int requestedFps = 30;
    static volatile int requestedPort = 47802;

    // The consent result, handed from the prompt's activity to the service. Same process, so a
    // static is enough, and it avoids the getParcelableExtra signature that changed in API 33
    // (PICO runs API 29). One-shot: Android rejects a second getMediaProjection with it anyway.
    private static int pendingResultCode;
    private static Intent pendingData;

    private static final Handler mainHandler = new Handler(Looper.getMainLooper());
    private static Activity hostActivity;
    private static boolean watching;
    private static boolean hostStopped;
    private static long stopAfterBackgroundMs = 20000;
    private static final Runnable stopWhileAway = new Runnable() {
        @Override
        public void run() {
            Log.i(TAG, "The app stayed in the background, so the capture is stopping.");
            stop();
        }
    };

    private HexRScreenCapture() {
    }

    /**
     * Asks the participant to allow capture and, if they do, starts serving the headset view on
     * the video port. Does nothing while a session is being asked for or is already running.
     *
     * stopAfterBackgroundSeconds: if the Unity activity stays stopped this long (another app in
     * front, or the headset asleep), the session ends rather than keep capturing whatever is on
     * screen now, and keep the ports, with nobody in this app to see it.
     */
    public static void start(final Activity activity, int width, int height, int bitrateKbps, int fps,
                             int videoPort, int stopAfterBackgroundSeconds) {
        if (activity == null) {
            setState(STATE_ERROR, "No activity to show the capture prompt from.");
            return;
        }

        if (STATE_REQUESTING.equals(state) || STATE_LIVE.equals(state)) {
            return;
        }

        // The encoder works in 16-pixel macroblocks; a size off that grid is a configure failure
        // on some encoders and a green edge on others.
        requestedWidth = roundTo16(width, 1024);
        requestedHeight = roundTo16(height, 1024);
        requestedBitrate = Math.max(250, bitrateKbps) * 1000;
        requestedFps = Math.max(5, Math.min(60, fps));
        requestedPort = videoPort;
        stopAfterBackgroundMs = Math.max(1, stopAfterBackgroundSeconds) * 1000L;

        hostActivity = activity;
        watchLifecycle(activity.getApplication());
        setState(STATE_REQUESTING, "Waiting for the participant to allow screen sharing in the headset.");

        activity.runOnUiThread(new Runnable() {
            @Override
            public void run() {
                try {
                    Intent intent = new Intent(activity, ProjectionRequestActivity.class);
                    activity.startActivity(intent);
                    activity.overridePendingTransition(0, 0);
                } catch (Throwable t) {
                    Log.e(TAG, "Could not open the capture prompt", t);
                    setState(STATE_ERROR, "Could not open the capture prompt: " + t.getMessage());
                }
            }
        });
    }

    /** Ends the session, if there is one. Phones watching see their stream close. */
    public static void stop() {
        mainHandler.removeCallbacks(stopWhileAway);
        CaptureService service = CaptureService.current();
        if (service != null) {
            service.requestStop(STATE_OFF, "");
        } else if (STATE_REQUESTING.equals(state)) {
            // The prompt may still be up. Its answer arrives later and is dropped (see onConsent).
            setState(STATE_OFF, "");
        }
    }

    public static String getState() {
        return state;
    }

    public static String getMessage() {
        return message;
    }

    public static int getWidth() {
        return width;
    }

    public static int getHeight() {
        return height;
    }

    public static int getPort() {
        return port;
    }

    public static int getViewerCount() {
        return viewers;
    }

    // ---- called by the prompt activity and the service ---------------------------------------

    static void onConsent(Activity from, int resultCode, Intent data) {
        if (!STATE_REQUESTING.equals(state)) {
            // Stopped while the prompt was up. Starting now would surprise everyone.
            Log.i(TAG, "Capture answer arrived after the request was withdrawn; ignoring it.");
            return;
        }

        if (resultCode != Activity.RESULT_OK || data == null) {
            setState(STATE_DENIED, "The participant did not allow screen sharing.");
            return;
        }

        synchronized (HexRScreenCapture.class) {
            pendingResultCode = resultCode;
            pendingData = data;
        }

        // Only now: on Android 14 a mediaProjection foreground service may not start before the
        // user has said yes, and starting one early throws.
        try {
            from.startForegroundService(new Intent(from, CaptureService.class));
        } catch (Throwable t) {
            synchronized (HexRScreenCapture.class) {
                pendingData = null;
            }
            Log.e(TAG, "Could not start the capture service", t);
            setState(STATE_ERROR, "Could not start the capture service: " + t.getMessage());
        }
    }

    static void onPromptFailed(Throwable t) {
        Log.e(TAG, "The capture prompt could not be shown", t);
        setState(STATE_ERROR, "This headset could not show the capture prompt: " + t.getMessage());
    }

    /** The consent result, once. Null if there is none waiting. */
    static synchronized Intent takePendingData() {
        Intent data = pendingData;
        pendingData = null;
        return data;
    }

    static synchronized int pendingResultCode() {
        return pendingResultCode;
    }

    static void setState(String newState, String newMessage) {
        state = newState;
        message = newMessage == null ? "" : newMessage;
        if (!STATE_LIVE.equals(newState)) {
            viewers = 0;
            mainHandler.removeCallbacks(stopWhileAway);
        } else if (hostStopped) {
            mainHandler.postDelayed(stopWhileAway, stopAfterBackgroundMs);
        }

        Log.i(TAG, "Capture " + newState + (message.isEmpty() ? "" : ": " + message));
    }

    static void setSize(int w, int h) {
        width = w;
        height = h;
    }

    static void setPort(int p) {
        port = p;
    }

    static void setViewers(int count) {
        viewers = count;
    }

    private static int roundTo16(int value, int fallback) {
        if (value <= 0) {
            value = fallback;
        }

        return Math.max(16, (value + 8) / 16 * 16);
    }

    /**
     * Ends the session when the Unity activity goes away for good, or stays in the background
     * longer than the app allows. The service would otherwise keep the process alive and keep
     * capturing whatever is on screen after this app has gone.
     *
     * The prompt itself sends the Unity activity into the background on some headsets (on Quest it
     * opens as a panel), so a stop during "requesting" is not counted.
     */
    private static void watchLifecycle(Application application) {
        if (watching || application == null) {
            return;
        }

        watching = true;
        application.registerActivityLifecycleCallbacks(new Application.ActivityLifecycleCallbacks() {
            @Override
            public void onActivityStarted(Activity activity) {
                if (activity == hostActivity) {
                    hostStopped = false;
                    mainHandler.removeCallbacks(stopWhileAway);
                }
            }

            @Override
            public void onActivityStopped(Activity activity) {
                if (activity != hostActivity) {
                    return;
                }

                hostStopped = true;
                if (STATE_LIVE.equals(state)) {
                    mainHandler.removeCallbacks(stopWhileAway);
                    mainHandler.postDelayed(stopWhileAway, stopAfterBackgroundMs);
                }
            }

            @Override
            public void onActivityDestroyed(Activity activity) {
                if (activity == hostActivity && !activity.isChangingConfigurations()) {
                    Log.i(TAG, "The app's activity was destroyed, so the capture is stopping.");
                    stop();
                    hostActivity = null;
                }
            }

            @Override
            public void onActivityCreated(Activity activity, Bundle savedInstanceState) {
            }

            @Override
            public void onActivityResumed(Activity activity) {
            }

            @Override
            public void onActivityPaused(Activity activity) {
            }

            @Override
            public void onActivitySaveInstanceState(Activity activity, Bundle outState) {
            }
        });
    }
}
