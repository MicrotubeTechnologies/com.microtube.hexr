using HaptGlove;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace HexR
{
    /// <summary>
    /// One-shot scene wiring, run from HexR > Troubleshoot > Re-run Auto Setup, from Create HexR Rig, and from the
    /// Manager Inspector's button.
    ///
    /// Moved out of HexRManager, where it sat behind #if UNITY_EDITOR in a runtime file. It is
    /// editor-only -- nothing calls it at runtime -- so it belongs in the editor assembly, and
    /// moving it takes System.Linq and UnityEngine.UI out of the runtime file with it.
    ///
    /// The helpers it drives stay on HexRManager because RebindHand calls them on every scene
    /// load. They are `internal`, reached across the assembly boundary through
    /// InternalsVisibleTo("HexR.Editor") in Runtime/HexR/AssemblyInfo.cs, rather than made public
    /// -- they are implementation detail, not supported surface.
    /// </summary>
    public static class HexRAutoSetup
    {
        // Extracted so both the Inspector's "Auto Set Up HexR" button and the top-level
        // HexR > Troubleshoot > Re-run Auto Setup menu item (Editor/HexRMenu.cs) run the exact same
        // logic instead of it being duplicated in two places.
        public static void Run(HexRManager controller)
        {
            try
            {
                controller.rightHand = GameObject.Find("Right Hand Physics").GetComponent<HaptGloveHandler>();
                controller.leftHand = GameObject.Find("Left Hand Physics").GetComponent<HaptGloveHandler>();
                Debug.Log("Right Hand Physics Found And Assigned.");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[HexR] AutoSetup: couldn't find/assign Left/Right Hand Physics -- " + e.Message + ". Remember to assign them manually.");
            }

            if (controller.XRFramework == HexRManager.Options.OpenXR)
            {
                // The menu is part of the rig prefab now -- nothing to instantiate here.
                try
                {
                    // The status texts, the indicator dots and HexRPanel itself are assigned by
                    // HexRFloatingMenu, which builds them, rather than found here by name. Name
                    // matching only ever worked against the authored panel's exact hierarchy, and
                    // broke the moment anyone renamed a child.

                    Debug.Log("[HexR] OpenXR rig set up.");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[HexR] AutoSetup: HexR panel is not set up -- " + e.Message + ". Manual set up needed.");
                }
                //Set up hand menu bluetooth buttons
                try
                {
                    Button RightBluetoothButton = HexRCompat.FindAll<GameObject>(true).FirstOrDefault(obj => obj.name == "Right Bluetooth Button").GetComponent<Button>();
                    Button LeftBluetoothButton = HexRCompat.FindAll<GameObject>(true).FirstOrDefault(obj => obj.name == "Left Bluetooth Button").GetComponent<Button>();

                    RightBluetoothButton.onClick.AddListener(controller.ConnectRightBT);
                    LeftBluetoothButton.onClick.AddListener(controller.ConnectLeftBT);
                    Debug.Log("HexR panel button set up complete.");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[HexR] AutoSetup: HexR panel button is not set up -- " + e.Message + ". Manual set up needed.");
                }
                // Find hand root for physics hand
                try
                {
                    GameObject LeftXR = GameObject.Find("Left Hand Interaction Visual");
                    GameObject RightXR = GameObject.Find("Right Hand Interaction Visual");
                    HexRTrackedHand LeftP = controller.leftHand.gameObject.GetComponent<HexRTrackedHand>();
                    HexRTrackedHand RightP = controller.rightHand.gameObject.GetComponent<HexRTrackedHand>();
                    LeftP.handRoot = LeftXR.transform.Find("L_Wrist");
                    RightP.handRoot = RightXR.transform.Find("R_Wrist");
                    EditorUtility.SetDirty(LeftP); // Mark as dirty to save changes
                    EditorUtility.SetDirty(RightP); // Mark as dirty to save changes
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[HexR] AutoSetup: XR hand is not linked to HexRTrackedHand -- " + e.Message + ". Manual link needed: drag the hand root of your VR hand to the left and right HexRTrackedHand script.");
                }
            }

            else if (controller.XRFramework == HexRManager.Options.MetaOVR)
            {
                //Set up HexR Panel
                try
                {
                    // The status texts, the indicator dots and HexRPanel itself are assigned by
                    // HexRFloatingMenu, which builds them, rather than found here by name. Name
                    // matching only ever worked against the authored panel's exact hierarchy, and
                    // broke the moment anyone renamed a child.

                    Debug.Log("[HexR] Meta OVR rig set up.");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[HexR] AutoSetup: HexR panel is not set up -- " + e.Message + ". Manual set up needed.");
                }
                // Find hand root for physics hand
                try
                {
                    HexRTrackedHand LeftP = controller.leftHand.gameObject.GetComponent<HexRTrackedHand>();
                    HexRTrackedHand RightP = controller.rightHand.gameObject.GetComponent<HexRTrackedHand>();
                    LeftP.handRoot = null;
                    RightP.handRoot = null;

                    // "OpenXRLeftHand/OpenXRRightHand" first: from com.meta.xr.sdk.interaction
                    // v201 on, HandVisual.Awake deactivates the legacy OculusHand_L/R bone rig
                    // and drives the OpenXR one instead, so pointing handRoot at OculusHand_L/R
                    // now hands HexRTrackedHand a root that goes inactive on Awake (and that
                    // GameObject.Find can no longer re-find when it nulls out).
                    // "OculusHand_L/R" is the legacy Oculus Integration naming, still correct on
                    // older SDKs. Projects built with Meta's "Building Blocks" hand-tracking
                    // block instead have "LeftOVRHand"/"RightOVRHand" (under "[BuildingBlock]
                    // Hand Tracking left/right") -- try that too rather than silently failing and
                    // leaving handRoot unassigned.
                    GameObject leftHandObj = HexRManager.FindHandVisualRoot("OpenXRLeftHand", "OculusHand_L", "LeftOVRHand");
                    GameObject rightHandObj = HexRManager.FindHandVisualRoot("OpenXRRightHand", "OculusHand_R", "RightOVRHand");

                    LeftP.handRoot = leftHandObj.transform;
                    RightP.handRoot = rightHandObj.transform;
                    EditorUtility.SetDirty(LeftP); // Mark as dirty to save changes
                    EditorUtility.SetDirty(RightP); // Mark as dirty to save changes

                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[HexR] AutoSetup: XR hand is not linked to HexRTrackedHand -- " + e.Message + ". Manual link needed: drag the hand root of your VR hand to the left and right HexRTrackedHand script.");
                }
            }

            // Add trigger colliders + HapticFingerTrigger to each fingertip/palm directly on
            // the raw tracked hand -- this is now the one and only haptics/grab
            // touch-detection path (see HexRTrackedHand.ResolveRawFingerJoint /
            // ResolveRawPalmJoint). Runs after handRoot is wired above, and is safe to
            // re-run: existing colliders are never touched, existing HapticFingerTrigger
            // components just get their config refreshed.
            HexRManager.AutoAddFingerHaptics(controller);

            // Wires handType (and, for Meta OVR, the grab/poke interactors used to gate
            // hand-near haptics) on each "Left/Right Pressure Controller" -- previously only
            // validated as present, never actually configured by Auto Setup.
            HexRManager.AutoSetupPressureControllers(controller);

            // One visualizer for the whole rig, on the manager. It looks both hands up through
            // HexRManager, so this single instance draws every tracked-hand and ghost-rig
            // collider -- and, unlike the per-hand-root ones this replaces, it survives a scene
            // change along with the manager.
            HexRManager.EnsureColliderVisualizer(controller.gameObject);

            EditorUtility.SetDirty(controller); // Mark as dirty to save changes

            HexRSetupValidator.Run(controller);
        }
    }
}
