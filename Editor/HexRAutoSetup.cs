using HaptGlove;
using UnityEditor;
using UnityEngine;

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
            // The rig's two hands are whichever objects carry a HexRTrackedHand, found by
            // component rather than by name: the packaged rigs call them "Left/Right Hand
            // Physics", a project's own copy may not (the PICO tutorial's are "Left/Right
            // Pressure Controller"), and a name lookup that misses fails silently.
            HexRTrackedHand left = FindRigHand(controller, HexRTrackedHand.HandType.Left);
            HexRTrackedHand right = FindRigHand(controller, HexRTrackedHand.HandType.Right);
            if (left != null) controller.leftHand = left.GetComponent<HaptGloveHandler>();
            if (right != null) controller.rightHand = right.GetComponent<HaptGloveHandler>();
            if (controller.leftHand == null || controller.rightHand == null)
            {
                Debug.LogWarning("[HexR] AutoSetup: couldn't find both hands on the rig -- each needs a HaptGloveHandler and a "
                    + "HexRTrackedHand with its Hand Type set. Assign HexRManager's Left/Right Hand manually.");
            }

            // Point each hand at the SDK's tracked hand. Same lookup the scene-load rebind uses,
            // so an Editor-time setup and a runtime rebind can't land on different objects.
            LinkTrackedHand(left);
            LinkTrackedHand(right);

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
            // HexRManager, so this single instance draws every tracked-hand collider -- and,
            // unlike the per-hand-root ones this replaces, it survives a scene change along with
            // the manager.
            HexRManager.EnsureColliderVisualizer(controller.gameObject);

            EditorUtility.SetDirty(controller); // Mark as dirty to save changes

            HexRSetupValidator.Run(controller);
        }

        private static HexRTrackedHand FindRigHand(HexRManager controller, HexRTrackedHand.HandType side)
        {
            foreach (HexRTrackedHand hand in controller.GetComponentsInChildren<HexRTrackedHand>(true))
            {
                if (hand.handType == side) return hand;
            }

            // A rig whose hands sit outside the manager's hierarchy.
            foreach (HexRTrackedHand hand in HexRCompat.FindAll<HexRTrackedHand>(true))
            {
                if (hand.handType == side) return hand;
            }
            return null;
        }

        private static void LinkTrackedHand(HexRTrackedHand hand)
        {
            if (hand == null) return;

            Transform root = HexRManager.FindTrackedHandRoot(hand.handType);
            if (root == null)
            {
                Debug.LogWarning("[HexR] AutoSetup: couldn't find a tracked " + hand.handType + " hand in the scene -- add a "
                    + "hand-tracking rig (Meta's OVRCameraRig hands, or XRI's XR Hands setup), or drag the tracked hand onto "
                    + hand.name + "'s HexRTrackedHand.handRoot.");
                return;
            }

            hand.handRoot = root;
            EditorUtility.SetDirty(hand);
        }
    }
}
