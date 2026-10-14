package com.microtube.hexr.remote;

import android.app.Activity;
import android.content.Intent;
import android.media.projection.MediaProjectionConfig;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Bundle;

/**
 * An invisible activity whose only job is to show Android's "allow screen sharing" prompt and
 * pass the answer on.
 *
 * The answer to that prompt comes back as an activity result, and the Unity activity cannot
 * receive one: some HexR apps run UnityPlayerActivity, others UnityPlayerGameActivity, and a
 * package can subclass neither. The ArduinoBluetoothAPI plugin hosts its Bluetooth dialogs the
 * same way (EmptyActivity), which is how we know this works inside an immersive app on Quest.
 */
public final class ProjectionRequestActivity extends Activity {

    private static final int REQUEST_CAPTURE = 0x4852;
    private static final String KEY_ASKED = "asked";

    // Survives a recreate while the prompt is up, so a rotation or config change does not ask twice.
    private boolean asked;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        if (savedInstanceState != null) {
            asked = savedInstanceState.getBoolean(KEY_ASKED, false);
        }

        if (asked) {
            return;
        }

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
