#if UNITY_ANDROID
// Only compiled with Android as the build target: UnityEditor.Android exists only where the
// Android module is installed, and a project without it must still compile the HexR editor code.
using System;
using System.IO;
using UnityEditor.Android;
using UnityEngine;

namespace HexR
{
    /// <summary>
    /// Keeps HexR Remote's Java classes in a minified Android build.
    ///
    /// C# reaches com.microtube.hexr.remote.HexRScreenCapture by name through JNI, and the
    /// activity and service are named only in the merged manifest, so R8 sees nothing using them
    /// and strips them. The build would succeed and sharing the view would then fail on the
    /// headset with ClassNotFound. None of the HexR apps minify today; this is for the first one
    /// that does. Same approach as Unity's own Oculus plugin (OculusBuildProcessor).
    /// </summary>
    internal sealed class HexRRemoteBuildProcessor : IPostGenerateGradleAndroidProject
    {
        private const string KeepRule = "-keep class com.microtube.hexr.remote.** { *; }";

        public int callbackOrder => 10000;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            try
            {
                string proguard = Path.Combine(path, "proguard-unity.txt");
                if (File.Exists(proguard) && !File.ReadAllText(proguard).Contains(KeepRule))
                {
                    File.AppendAllText(proguard, Environment.NewLine + KeepRule + Environment.NewLine);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[HexR] Could not add HexR Remote's keep rule to proguard-unity.txt: " + e.Message);
            }
        }
    }
}
#endif
