using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace HexR.Tests
{
    /// <summary>
    /// The rig survives scene loads, so a scene that carries its own copy loads a duplicate, which
    /// HexRManager.Awake switches off. That only helps if it runs before anything else on the copy
    /// wakes. Before 0.10.1 nothing ordered it: on every scene reload the copy's
    /// PointableCanvasModule took Meta's one-per-scene instance and, destroyed a frame later, left
    /// none for the canvases registering after it, and the copy's glove handlers threw shutting
    /// down Bluetooth they never opened.
    /// </summary>
    public class RigOrderTests
    {
        [TestCase("Packages/com.microtube.hexr/Runtime/Prefabs/HexR Main (OVR).prefab")]
        [TestCase("Packages/com.microtube.hexr/Runtime/Prefabs/HexR Main (Open XR).prefab")]
        public void TheManagerWakesBeforeEverythingElseOnTheRig(string path)
        {
            GameObject rig = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(rig, path);
            int manager = OrderOf(rig.GetComponent<HexRManager>());

            foreach (MonoBehaviour behaviour in rig.GetComponentsInChildren<MonoBehaviour>(true))
            {
                // Null where the backend this prefab is for is not installed: nothing to wake.
                if (behaviour == null || behaviour is HexRManager)
                {
                    continue;
                }

                Assert.Less(manager, OrderOf(behaviour), behaviour.GetType().Name + " on " + behaviour.name
                    + " would wake on a duplicate rig before the manager switched it off.");
            }
        }

        // What Unity runs it at: the project's Script Execution Order setting when there is one
        // (Unity's own templates put EventSystem at -1000 there), otherwise the script's
        // [DefaultExecutionOrder], which MonoImporter does not report.
        private static int OrderOf(MonoBehaviour behaviour)
        {
            int set = MonoImporter.GetExecutionOrder(MonoScript.FromMonoBehaviour(behaviour));
            if (set != 0)
            {
                return set;
            }

            object[] attributes = behaviour.GetType().GetCustomAttributes(typeof(DefaultExecutionOrder), true);
            return attributes.Length > 0 ? ((DefaultExecutionOrder)attributes[0]).order : 0;
        }
    }
}
