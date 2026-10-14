using System;
using UnityEngine;

namespace HexR
{
    /// <summary>What HexRRemote needs from screen capture, so tests can stand one in.</summary>
    internal interface IRemoteStream
    {
        void Start();
        void Stop();
        RemoteStreamStatus Read();
    }

    internal static class RemoteStreams
    {
        public static IRemoteStream Create(HexRRemote settings)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return new AndroidRemoteStream(settings);
#else
            return new UnsupportedRemoteStream();
#endif
        }
    }

    /// <summary>
    /// The Editor and desktop builds have no MediaProjection. Saying so in the status lets the
    /// phone show "this headset can't share its view" instead of a Start button that does nothing.
    /// </summary>
    internal sealed class UnsupportedRemoteStream : IRemoteStream
    {
        public void Start()
        {
        }

        public void Stop()
        {
        }

        public RemoteStreamStatus Read()
        {
            return new RemoteStreamStatus
            {
                state = RemoteStreamState.Unsupported,
                message = "Sharing the view needs the app running on the headset.",
            };
        }
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    /// <summary>
    /// Drives the Java side in Runtime/Plugins/Android/HexRRemote. Every call is made from the
    /// main thread (HexRRemote.Update), which is also the only thread that has Unity's activity.
    /// </summary>
    internal sealed class AndroidRemoteStream : IRemoteStream
    {
        private const string CaptureClass = "com.microtube.hexr.remote.HexRScreenCapture";
        private readonly HexRRemote settings;
        private AndroidJavaClass capture;
        private bool failed;

        public AndroidRemoteStream(HexRRemote settings)
        {
            this.settings = settings;
        }

        public void Start()
        {
            AndroidJavaClass cls = Capture();
            if (cls == null)
            {
                return;
            }

            try
            {
                using (AndroidJavaClass player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    cls.CallStatic("start", activity, settings.videoWidth, settings.videoHeight,
                        settings.videoBitrateKbps, settings.videoFps, settings.videoPort,
                        settings.stopVideoAfterBackgroundSeconds);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[HexR] Remote: could not start sharing the view: " + e);
            }
        }

        public void Stop()
        {
            AndroidJavaClass cls = Capture();
            if (cls == null)
            {
                return;
            }

            try
            {
                cls.CallStatic("stop");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[HexR] Remote: stopping the view share failed: " + e);
            }
        }

        public RemoteStreamStatus Read()
        {
            AndroidJavaClass cls = Capture();
            if (cls == null)
            {
                return new RemoteStreamStatus
                {
                    state = RemoteStreamState.Unsupported,
                    message = "This build is missing HexR's screen capture plugin.",
                };
            }

            try
            {
                return new RemoteStreamStatus
                {
                    state = cls.CallStatic<string>("getState"),
                    message = cls.CallStatic<string>("getMessage"),
                    port = cls.CallStatic<int>("getPort"),
                    width = cls.CallStatic<int>("getWidth"),
                    height = cls.CallStatic<int>("getHeight"),
                    viewers = cls.CallStatic<int>("getViewerCount"),
                };
            }
            catch (Exception e)
            {
                return new RemoteStreamStatus { state = RemoteStreamState.Error, message = e.Message };
            }
        }

        /// <summary>
        /// Null if the Java class is missing (an app that strips it with minify, or a package
        /// copy without the plugin). Logged once, not five times a second.
        /// </summary>
        private AndroidJavaClass Capture()
        {
            if (capture != null || failed)
            {
                return capture;
            }

            try
            {
                capture = new AndroidJavaClass(CaptureClass);
            }
            catch (Exception e)
            {
                failed = true;
                Debug.LogError("[HexR] Remote: " + CaptureClass + " is not in this build: " + e.Message);
            }

            return capture;
        }
    }
#endif
}
