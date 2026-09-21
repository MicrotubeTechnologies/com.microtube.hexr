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
    /// It deliberately does not write the result into the fields. Doing that would serialise a
    /// scene reference that then goes stale the moment the rig changes, and would defeat the
    /// runtime lookup that already handles a rig loading late or arriving with a different scene.
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
                    + "not be felt.\n\nRun HexR > Auto Setup Scene, or add a HexR rig.",
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
        }
    }
}
