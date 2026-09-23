using System;
using UnityEditor;
using UnityEngine;

namespace HexR
{
    /// <summary>
    /// Inspector for <see cref="HexRInteractableHaptics"/> that shows what will actually drive it
    /// and which Pressure Controllers it will use.
    ///
    /// Why this exists. The component finds both itself at Start, so the two tracker fields are
    /// meant to stay empty -- but an empty object field reads as "you forgot to set this", and the
    /// natural response is to go hunting for something to drag in. This says so plainly instead,
    /// and shows what the lookup currently resolves to.
    ///
    /// Leaving the fields empty is the default, and the right choice for most scenes: the
    /// runtime lookup copes with a rig that loads late or arrives with a different scene, and a
    /// serialised reference goes stale the moment the rig changes. For projects that want the
    /// references pinned anyway -- the habit SpecialHaptics' "Auto Find Hand Physics" serves --
    /// an Auto Find button fills both fields with what the lookup resolves to, and Clear returns
    /// to finding them at play.
    ///
    /// Everything here is backend-neutral: it asks the registry rather than naming an SDK type, so
    /// this file lives in HexR.Editor beside the rest of the package tooling rather than needing an
    /// assembly per backend.
    /// </summary>
    [CustomEditor(typeof(HexRInteractableHaptics))]
    [CanEditMultipleObjects]
    public class HexRInteractableHapticsEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (targets.Length > 1)
            {
                return;
            }

            HexRInteractableHaptics self = (HexRInteractableHaptics)target;
            EditorGUILayout.Space();

            DrawBackend(self);
            DrawDoubleWiringWarning(self);
            DrawTrackers(self);
        }

        private static void DrawBackend(HexRInteractableHaptics self)
        {
            string diagnostic;
            IHexRInteractionBackend backend =
                HexRInteractionBackends.Resolve(self.backend, self.gameObject, out diagnostic);

            if (backend == null)
            {
                EditorGUILayout.HelpBox(diagnostic, MessageType.Warning);
                return;
            }

            // CanBind rather than Bind: the Inspector must not subscribe to anything.
            bool canBind = backend.CanBind(self.gameObject);

            EditorGUILayout.HelpBox(
                "Driven by " + backend.DisplayName + "."
                + (canBind
                    ? "\n\nFound an interactable on this object."
                    : "\n\nNo interactable found on this object, so nothing will be felt. Add the "
                      + "interactable your SDK uses -- an XR Grab Interactable on OpenXR, a Grabbable "
                      + "or Hand Grab Interactable on Meta."),
                canBind ? MessageType.Info : MessageType.Warning);

            // Resolve returns a usable backend and still reports a problem when an explicit choice
            // could not be honoured, so surface that too rather than letting it look fine.
            if (!string.IsNullOrEmpty(diagnostic))
            {
                EditorGUILayout.HelpBox(diagnostic, MessageType.Warning);
            }
        }

        /// <summary>
        /// Catches the pattern this component replaces: a UnityEvent on the object wired straight
        /// to a Pressure Controller, usually <c>TriggerAllHapticsIncrease</c> on a
        /// PointableUnityEventWrapper. Left in place alongside this component, the two fight --
        /// the direct call drives both hands unconditionally and this one drives the hand that
        /// actually grabbed, and whichever ran last wins.
        ///
        /// Done through SerializedObject rather than by naming UnityEvent types, so it needs no
        /// reference to any SDK and catches the pattern wherever it appears.
        /// </summary>
        private static void DrawDoubleWiringWarning(HexRInteractableHaptics self)
        {
            string offender = FindDirectPressureTrackerCall(self);
            if (offender == null)
            {
                return;
            }

            EditorGUILayout.HelpBox(
                "\"" + offender + "\" on this object calls a Pressure Controller directly from a "
                + "UnityEvent. That will fight with this component -- the direct call drives both "
                + "hands at once, this one drives the hand that actually grabbed.\n\n"
                + "Clear those persistent calls and let this component do it.",
                MessageType.Warning);
        }

        private static string FindDirectPressureTrackerCall(HexRInteractableHaptics self)
        {
            MonoBehaviour[] all = self.gameObject.GetComponentsInChildren<MonoBehaviour>(true);

            foreach (MonoBehaviour mb in all)
            {
                if (mb == null || mb is HexRInteractableHaptics)
                {
                    continue;
                }

                using (SerializedObject so = new SerializedObject(mb))
                {
                    SerializedProperty it = so.GetIterator();
                    while (it.Next(true))
                    {
                        if (it.propertyType != SerializedPropertyType.ObjectReference)
                        {
                            continue;
                        }

                        if (it.propertyPath.IndexOf("m_PersistentCalls", StringComparison.Ordinal) < 0)
                        {
                            continue;
                        }

                        if (it.objectReferenceValue is PressureTrackerMain)
                        {
                            return mb.GetType().Name;
                        }
                    }
                }
            }

            return null;
        }

        private static void DrawTrackers(HexRInteractableHaptics self)
        {
            // Ask the scene the same question the component asks at Start, without touching it.
            PressureTrackerMain left = self.leftPressureTracker;
            PressureTrackerMain right = self.rightPressureTracker;
            bool overridden = left != null || right != null;

            if (!overridden)
            {
                foreach (PressureTrackerMain t in HexRCompat.FindAll<PressureTrackerMain>(true))
                {
                    if (left == null && t.handType == PressureTrackerMain.HandType.Left)
                    {
                        left = t;
                    }
                    else if (right == null && t.handType == PressureTrackerMain.HandType.Right)
                    {
                        right = t;
                    }
                }
            }

            if (left == null && right == null)
            {
                EditorGUILayout.HelpBox(
                    "No Pressure Controllers in this scene, so this object will be grabbable but will "
                    + "not be felt.\n\nRun HexR > Troubleshoot > Re-run Auto Setup, or add a HexR rig.",
                    MessageType.Warning);
                return;
            }

            EditorGUILayout.HelpBox(
                (overridden
                    ? "Using the Pressure Controllers set above."
                    : "Found automatically at play -- nothing to drag in.")
                + "\n\nLeft:  " + (left != null ? left.gameObject.name : "none")
                + "\nRight: " + (right != null ? right.gameObject.name : "none"),
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Auto Find Pressure Controllers",
                    "Fill Left/Right Pressure Tracker with the Pressure Controllers in this scene, the same "
                    + "way the component finds them at play. Pinned references go stale if the rig changes.")))
                {
                    Undo.RecordObject(self, "Auto Find Pressure Controllers");
                    // Same lookup the component runs at Start: by handType when the two disagree,
                    // then by the side in the name. It only fills what is still empty.
                    if (!self.ResolveTrackers())
                    {
                        Debug.LogWarning("[HexR] " + self.name + ": no Pressure Controllers in this scene to find. "
                            + "Run HexR > Troubleshoot > Re-run Auto Setup, or add a HexR rig.", self);
                    }
                    EditorUtility.SetDirty(self);
                }

                using (new EditorGUI.DisabledScope(!overridden))
                {
                    if (GUILayout.Button(new GUIContent("Clear", "Empty both fields and find them at play again.")))
                    {
                        Undo.RecordObject(self, "Clear Pressure Controllers");
                        self.leftPressureTracker = null;
                        self.rightPressureTracker = null;
                        EditorUtility.SetDirty(self);
                    }
                }
            }
        }
    }
}
