using HaptGlove;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace HexR
{
    // Moved out of HexRManager.cs, where it lived as a nested class behind #if UNITY_EDITOR.
    // It is editor-only by nature and HexR.Editor is where editor-only code belongs; keeping it in
    // the runtime file meant 134 lines of Inspector chrome compiled into every player build's
    // source tree and read as part of the manager.
    //
    // Moved verbatim apart from de-indenting and the usings. It writes fields directly on the
    // target rather than through SerializedObject, so there is still no Undo, no multi-object
    // edit and no prefab-override bolding -- worth fixing, but not while also moving it.
    [CustomEditor(typeof(HexRManager))]
    public class HexRSettingEditorGUI : Editor
    {
        // Foldout open/closed state -- plain instance fields, not serialized. Resets to
        // these defaults on reselection/domain reload, which is fine for editor-only UI
        // state; Setup starts open since that's what you touch first on a fresh rig,
        // the tuning/panel sections start collapsed since they're occasional-use.
        private bool showSetup = true;
        private bool showColliderTuning = false;
        private bool showPanelUI = false;

        public override void OnInspectorGUI()
        {
            HexRManager controller = (HexRManager)target;

            showSetup = EditorGUILayout.BeginFoldoutHeaderGroup(showSetup, "XR Framework & Hand Physics");
            if (showSetup)
            {
                EditorGUI.indentLevel++;
                controller.XRFramework = (HexRManager.Options)EditorGUILayout.EnumPopup(
                    new GUIContent("XR Framework", "Which hand-joint naming convention this rig reads -- not which headset it runs on. "
                        + "OpenXR covers Unity XR Hands on any OpenXR runtime (Quest, PICO, Vive, SteamVR), because they all expose the same L_/R_ joint names. "
                        + "MetaOVR is for Meta's own Interaction SDK skeleton. There is deliberately no PICO option: PICO is OpenXR."),
                    controller.XRFramework);

                controller.isQuest = EditorGUILayout.Toggle(
                    new GUIContent("Quest BLE Buffering", "Forwarded to both hands' HaptGloveHandler at Start, where it selects a Bluetooth write-buffering strategy inside HaptGlove.dll. "
                        + "Correct for Quest, and on by default on both shipped rigs. UNTESTED on PICO: if a PICO build pairs with the glove but no haptics arrive, this is the first thing to turn off."),
                    controller.isQuest);

                GUILayout.Space(4);
                controller.rightHand = (HaptGloveHandler)EditorGUILayout.ObjectField(
                    new GUIContent("Right Hand Physics", "The right hand's HaptGloveHandler (glove/haptics object). Auto Setup finds this via GameObject.Find(\"Right Hand Physics\") and wires everything else -- colliders, panel, validation -- relative to it."),
                    controller.rightHand, typeof(HaptGloveHandler), true);
                controller.leftHand = (HaptGloveHandler)EditorGUILayout.ObjectField(
                    new GUIContent("Left Hand Physics", "The left hand's HaptGloveHandler (glove/haptics object). Auto Setup finds this via GameObject.Find(\"Left Hand Physics\") and wires everything else -- colliders, panel, validation -- relative to it."),
                    controller.leftHand, typeof(HaptGloveHandler), true);

                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            GUILayout.Space(6);

            showColliderTuning = EditorGUILayout.BeginFoldoutHeaderGroup(showColliderTuning, "Fingertip & Palm Collider Tuning");
            if (showColliderTuning)
            {
                EditorGUI.indentLevel++;
                controller.FingertipColliderRadius = EditorGUILayout.FloatField(
                    new GUIContent("Fingertip Collider Radius", "Radius of the sphere trigger collider Auto Setup adds to each fingertip on the raw tracked hand."),
                    controller.FingertipColliderRadius);

                GUILayout.Space(4);
                EditorGUILayout.LabelField(
                    new GUIContent("Fingertip Centers (Left, Meta)", "Local-space center offset of each left-hand fingertip's sphere collider, relative to that finger's own joint origin. Tune per-finger -- fingers aren't interchangeable and the rig isn't necessarily symmetric."),
                    EditorStyles.boldLabel);
                DrawFingertipCenters(controller.LeftFingertipCenters);

                GUILayout.Space(5);
                EditorGUILayout.LabelField(
                    new GUIContent("Fingertip Centers (Right, Meta)", "Same as Left, for the right hand. Tune independently -- the raw hand rig isn't guaranteed to be mirror-symmetric in local space, so left-hand values don't reliably carry over."),
                    EditorStyles.boldLabel);
                DrawFingertipCenters(controller.RightFingertipCenters);

                GUILayout.Space(5);
                controller.PalmColliderSize = EditorGUILayout.Vector3Field(
                    new GUIContent("Palm Collider Size", "Local-space box size of the trigger collider Auto Setup adds to the palm on the raw tracked hand."),
                    controller.PalmColliderSize);

                GUILayout.Space(8);
                EditorGUILayout.LabelField(
                    new GUIContent("XR Hands skeleton (PICO, OpenXR)", "Used instead of the values above when the tracked hand is Unity XR Hands' L_*/R_* skeleton rather than Meta's XRHand_* one. Its joint axes differ, so the offsets do too."),
                    EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Fingertip Centers (Left)");
                DrawFingertipCenters(controller.XRHandsLeftFingertipCenters);
                EditorGUILayout.LabelField("Fingertip Centers (Right)");
                DrawFingertipCenters(controller.XRHandsRightFingertipCenters);
                controller.XRHandsPalmColliderCenter = EditorGUILayout.Vector3Field("Palm Collider Center", controller.XRHandsPalmColliderCenter);
                controller.XRHandsPalmColliderSize = EditorGUILayout.Vector3Field("Palm Collider Size", controller.XRHandsPalmColliderSize);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            GUILayout.Space(6);

            showPanelUI = EditorGUILayout.BeginFoldoutHeaderGroup(showPanelUI, "HexR Panel UI Components");
            if (showPanelUI)
            {
                EditorGUI.indentLevel++;
                controller.BluetoothIndicatorL = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent("Bluetooth Indicator L", "Visual indicator shown once the left glove is connected over Bluetooth."),
                    controller.BluetoothIndicatorL, typeof(GameObject), true);
                controller.BluetoothIndicatorR = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent("Bluetooth Indicator R", "Visual indicator shown once the right glove is connected over Bluetooth."),
                    controller.BluetoothIndicatorR, typeof(GameObject), true);
                controller.pumpIndicator_L = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent("Pump Indicator L", "Visual indicator shown while the left glove's air pump is actively running."),
                    controller.pumpIndicator_L, typeof(GameObject), true);
                controller.pumpIndicator_R = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent("Pump Indicator R", "Visual indicator shown while the right glove's air pump is actively running."),
                    controller.pumpIndicator_R, typeof(GameObject), true);
                controller.LeftBtText = (TextMeshProUGUI)EditorGUILayout.ObjectField(
                    new GUIContent("Left Bluetooth Text", "Status text on the HexR panel reflecting the left glove's connection state (e.g. \"Searching...\", \"Left Glove Connected\")."),
                    controller.LeftBtText, typeof(TextMeshProUGUI), true);
                controller.RightBtText = (TextMeshProUGUI)EditorGUILayout.ObjectField(
                    new GUIContent("Right Bluetooth Text", "Status text on the HexR panel reflecting the right glove's connection state (e.g. \"Searching...\", \"Right Glove Connected\")."),
                    controller.RightBtText, typeof(TextMeshProUGUI), true);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            GUILayout.Space(15);

            if (GUILayout.Button(new GUIContent("Auto Set Up HexR", "Finds and wires everything above automatically -- hand roots, panel UI, fingertip/palm colliders -- then runs Validate.")))
            {
                HexRAutoSetup.Run(controller);
            }
            if (GUILayout.Button(new GUIContent("Validate HexR Setup", "Read-only check that everything above is wired correctly. Reports what's missing/misconfigured without changing anything -- safe to run any time.")))
            {
                HexRSetupValidator.Run(controller);
            }

            if (GUI.changed)
            {
                EditorUtility.SetDirty(target);
            }
        }

        private static void DrawFingertipCenters(HexRManager.FingertipCenters centers)
        {
            centers.Thumb = EditorGUILayout.Vector3Field(
                new GUIContent("Thumb"),
                centers.Thumb);
            centers.Index = EditorGUILayout.Vector3Field(new GUIContent("Index", "Center offset for the index fingertip's sphere collider."), centers.Index);
            centers.Middle = EditorGUILayout.Vector3Field(new GUIContent("Middle", "Center offset for the middle fingertip's sphere collider."), centers.Middle);
            centers.Ring = EditorGUILayout.Vector3Field(new GUIContent("Ring", "Center offset for the ring fingertip's sphere collider."), centers.Ring);
            centers.Little = EditorGUILayout.Vector3Field(new GUIContent("Little", "Center offset for the little (pinky) fingertip's sphere collider."), centers.Little);
        }
    }
}
