package com.microtube.hexr.remote;

import android.app.Activity;
import android.content.Intent;
import android.media.projection.MediaProjectionConfig;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Bundle;

/**
 * An invisible activity whose only job is to show Android's "allow screen sharing" prompt and
 * pass the answer on. When the app shares its sound and has never been allowed to, Android's
 * microphone prompt comes first: Android guards capturing even an app's own sound with it.
 *
 * The answer to that prompt comes back as an activity result, and the Unity activity cannot
 * receive one: some HexR apps run UnityPlayerActivity, others UnityPlayerGameActivity, and a
 * package can subclass neither. The ArduinoBluetoothAPI plugin hosts its Bluetooth dialogs the
 * same way (EmptyActivity), which is how we know this works inside an immersive app on Quest.
 */
public final class ProjectionRequestActivity extends Activity {

    private static final int REQUEST_CAPTURE = 0x4852;
    private static final int REQUEST_SOUND = 0x4853;
    private static final String KEY_ASKED = "asked";
    private static final String KEY_ASKED_SOUND = "askedSound";

    // Survive a recreate while a prompt is up, so a rotation or config change does not ask twice.
    private boolean asked;
    private boolean askedSound;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        if (savedInstanceState != null) {
            asked = savedInstanceState.getBoolean(KEY_ASKED, false);
            askedSound = savedInstanceState.getBoolean(KEY_ASKED_SOUND, false);
        }

        if (asked || askedSound) {
            return;
        }

        try {
            if (HexRScreenCapture.needsSoundPermission(this)) {
                askedSound = true;
                requestPermissions(new String[] { HexRScreenCapture.SOUND_PERMISSION }, REQUEST_SOUND);
                return;
            }
        } catch (Throwable t) {
            // No sound, then: CaptureService says why once the view is shared.
            askedSound = false;
        }

        askToCapture();
    }

    /**
     * Whatever the participant said about the microphone, the view is asked for next: a "no" there
     * costs the sound, not the view. Unless the phone withdrew the request meanwhile.
     */
    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != REQUEST_SOUND || asked) {
            return;
        }

        if (HexRScreenCapture.STATE_REQUESTING.equals(HexRScreenCapture.getState())) {
            askToCapture();
        } else {
            finishQuietly();
        }
    }

    private void askToCapture() {
        try {
            MediaProjectionManager manager = getSystemService(MediaProjectionManager.class);
            Intent prompt;
            if (Build.VERSION.SDK_INT >= 34) {
                // The whole headset view. Android 14 otherwise offers a "single app" choice, which
                // in an immersive app captures nothing useful.
                prompt = manager.createScreenCaptureIntent(MediaProjectionConfig.createConfigForDefaultDisplay());
            } else {
                prompt = manager.createScreenCaptureIntent();
            }

            startActivityForResult(prompt, REQUEST_CAPTURE);
            asked = true;
        } catch (Throwable t) {
            HexRScreenCapture.onPromptFailed(t);
            finishQuietly();
        }
    }

    @Override
    protected void onSaveInstanceState(Bundle outState) {
        super.onSaveInstanceState(outState);
        outState.putBoolean(KEY_ASKED, asked);
        outState.putBoolean(KEY_ASKED_SOUND, askedSound);
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQUEST_CAPTURE) {
            try {
                HexRScreenCapture.onConsent(this, resultCode, data);
            } catch (Throwable t) {
                HexRScreenCapture.onPromptFailed(t);
            }
        }

        finishQuietly();
    }

    private void finishQuietly() {
        finish();
        overridePendingTransition(0, 0);
    }
}
