#if UNITY_ANDROID
// Only compiled with Android as the build target: UnityEditor.Android exists only where the
// Android module is installed, and a project without it must still compile the HexR editor code.
using System;
using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

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

    /// <summary>
    /// Declares RECORD_AUDIO in a build that shares its sound: one with a HexR Remote in a build
    /// scene that has Allow Video and Share Sound on.
    ///
    /// Android guards all audio capture with that permission, even of the app's own sound, and
    /// Meta reviews it in Store submissions, so it is not in the package's manifest: an app that
    /// does not share its sound is built without it. A HexR Remote that no build scene holds (one
    /// made at runtime from a prefab) is not seen here; that app declares the permission itself.
    /// </summary>
    internal sealed class HexRRemoteSoundPermission : IPreprocessBuildWithReport, IProcessSceneWithReport,
        IPostGenerateGradleAndroidProject
    {
        private const string Permission = "android.permission.RECORD_AUDIO";
        private const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

        private static bool wanted;

        public int callbackOrder => 10000;

        public void OnPreprocessBuild(BuildReport report)
        {
            wanted = false;
        }

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            // No report means Play mode, which processes scenes too.
            if (report == null || wanted)
            {
                return;
            }

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (HexRRemote remote in root.GetComponentsInChildren<HexRRemote>(true))
                {
                    if (remote.allowVideo && remote.shareSound)
                    {
                        wanted = true;
                        return;
                    }
                }
            }
        }

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            if (!wanted)
            {
                return;
            }

            try
            {
                string file = Path.Combine(path, "src", "main", "AndroidManifest.xml");
                XmlDocument manifest = new XmlDocument();
                manifest.Load(file);
                foreach (XmlNode node in manifest.DocumentElement.ChildNodes)
                {
                    if (node is XmlElement e && e.Name == "uses-permission" &&
                        e.GetAttribute("name", AndroidNamespace) == Permission)
                    {
                        return;
                    }
                }

                XmlElement permission = manifest.CreateElement("uses-permission");
                XmlAttribute name = manifest.CreateAttribute("android", "name", AndroidNamespace);
                name.Value = Permission;
                permission.Attributes.Append(name);
                manifest.DocumentElement.PrependChild(permission);
                manifest.Save(file);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[HexR] Could not add RECORD_AUDIO for HexR Remote's sound, so the view will be shared without it: " +
                                 e.Message);
            }
        }
    }
}
#endif
