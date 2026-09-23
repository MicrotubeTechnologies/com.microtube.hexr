using HaptGlove;
using System.Collections.Generic;
using UnityEngine;

namespace HexR
{
    /// <summary>
    /// Checks a scene is wired correctly, for HexR > Troubleshoot > Validate Scene Setup, the HexR Tools window's
    /// Setup tab, and the tail of Auto Setup.
    ///
    /// Moved out of HexRManager: nothing calls it at runtime, and it uses no UnityEditor API at all
    /// -- it is pure Debug.Log and GetComponent -- so it moved verbatim. It reads
    /// HexRManager.Fingers rather than keeping its own copy, because a second list is exactly how
    /// you end up with a finger that gets wired but never validated, or the reverse, with no error
    /// either way.
    /// </summary>
    public static class HexRSetupValidator
    {
        // A single check's outcome -- lets tooling (the HexR Tools window's Setup tab)
        // render the same checks ValidateSetup logs to the console as a structured
        // checklist, instead of re-implementing the checks a second time and risking the
        // two copies drifting apart.
        public struct SetupCheck
        {
            public bool Ok;
            public string Title;
            public string Detail;

            public SetupCheck(bool ok, string title, string detail)
            {
                Ok = ok;
                Title = title;
                Detail = detail;
            }
        }

        // Read-only sanity pass over everything AutoSetup is supposed to wire up, run
        // automatically at the end of AutoSetup and also callable on its own (Inspector
        // button / HexR > Troubleshoot > Validate Scene Setup) to re-check a scene without redoing the
        // GameObject.Find-based rewiring above -- e.g. after someone hand-edits a hand root,
        // or on a scene AutoSetup was never run on in the first place. AutoSetup's own
        // try/catch blocks intentionally swallow failures and move on (a missing hand menu
        // shouldn't abort finding the hand roots), so this is the one place that adds up
        // everything left unassigned and reports it together instead of one log line at a
        // time buried in the console.
        //
        // results is optional -- pass a list to also collect a structured SetupCheck per
        // item (used by the HexR Tools window); existing callers that only want the
        // console log / bool can keep calling this with just controller.
        public static bool Run(HexRManager controller, List<SetupCheck> results = null)
        {
            bool ok = true;

            ok &= Check(results, controller.leftHand != null, "Left Hand Physics assigned", "Left Hand Physics (HaptGloveHandler) is not assigned.");
            ok &= Check(results, controller.rightHand != null, "Right Hand Physics assigned", "Right Hand Physics (HaptGloveHandler) is not assigned.");

            // The status texts, the indicator dots and HexRPanel are built and assigned by
            // HexRFloatingMenu when it runs, so checking the fields here would fail every scene
            // that has not been played. Check for the menu itself instead -- that is the thing
            // whose absence actually breaks anything, including the unguarded
            // HexRPanel.SetActive(...) in HaptGlove_OnConnectedFailed/OnDisconnected.
            ok &= Check(results,
                HexRCompat.FindAny<HexRFloatingMenu>(true) != null || controller.HexRPanel != null,
                "HexR menu present",
                "No HexR menu in this scene. Add one with HexR > Add HexR Menu, or use a rig prefab -- "
                + "without it there is no connect UI, and a failed or dropped connection throws.");

            if (controller.XRFramework == HexRManager.Options.OpenXR)
            {
                // Meta OVR has three hand-near sources and can lose this one without noticing;
                // OpenXR has only this one. With no ProximityCheck anywhere in the scene, an
                // OpenXR rig's IsHandNear is false forever and every haptic call that doesn't
                // pass ByPassHandCheck is silently dropped -- which looks exactly like broken
                // hardware, so it's worth failing setup over rather than leaving to discovery.
                ok &= Check(results,
                    HexRCompat.FindAll<ProximityCheck>(true).Length > 0,
                    "Proximity Check present (OpenXR)",
                    "No ProximityCheck in this scene. On OpenXR it is the only thing that sets hand-near, so haptics "
                    + "will never fire unless the call passes ByPassHandCheck. Add a ProximityCheck with a trigger "
                    + "collider to each object the hand should be able to feel.");
            }

            ok &= ValidateHand(controller.leftHand, HaptGloveHandler.HandType.Left, results);
            ok &= ValidateHand(controller.rightHand, HaptGloveHandler.HandType.Right, results);

            Debug.Log(ok
                ? "[HexR] Setup check passed -- all essential references are linked."
                : "[HexR] Setup check found issues -- see warnings/errors above.");
            return ok;
        }

        // Logs (matching prior behavior exactly) and, if results != null, records the
        // check for the Setup tab's checklist.
        private static bool Check(List<SetupCheck> results, bool ok, string title, string detail)
        {
            if (!ok)
            {
                Debug.LogWarning("[HexR] Setup check: " + detail);
            }
            results?.Add(new SetupCheck(ok, title, ok ? "OK" : detail));
            return ok;
        }

        private static bool ValidateHand(HaptGloveHandler hand, HaptGloveHandler.HandType expected, List<SetupCheck> results = null)
        {
            string label = expected == HaptGloveHandler.HandType.Left ? "Left" : "Right";
            if (hand == null) return true; // already reported by the caller

            HexRTrackedHand tracking = hand.GetComponent<HexRTrackedHand>();
            if (tracking == null)
            {
                return Check(results, false, label + " hand has HexRTrackedHand", label + " hand has no HexRTrackedHand component -- nothing tells HexR which tracked hand it is, so no haptics can be placed on it.");
            }

            bool ok = true;
            ok &= Check(results, tracking.handRoot != null, label + " hand's handRoot assigned",
                label + " hand's HexRTrackedHand.handRoot is not assigned -- no fingertip or palm haptics can be placed on it. Run HexR > Auto Setup Scene.");

            if (tracking.handRoot != null)
            {
                // Naming mismatch is a warning, not a hard failure -- flagged separately from
                // the "is it assigned at all" check above so both surface independently
                // (deliberately not folded into `ok`, matching the original behavior).
                Check(results, NameSuggestsHand(tracking.handRoot.name, expected), label + " hand's handRoot name looks right",
                    label + " hand's HexRTrackedHand.handRoot is '" + tracking.handRoot.name + "', which doesn't look like a " + label + "-hand object -- double-check this isn't wired to the other hand's transform.");
            }

            // HapticFingerTrigger/HexRGrabbable/SpecialHaptics all find this by
            // GameObject.Find("Left/Right Pressure Controller") at their own runtime Start()
            // -- not something AutoSetup wires or creates, so a missing/renamed one won't
            // show up as a broken reference anywhere else, just a NullReferenceException the
            // first time any fingertip HapticFingerTrigger fires. Flag it here instead.
            GameObject pressureControllerObj = GameObject.Find(label + " Pressure Controller");
            bool pressureControllerOk = pressureControllerObj != null && pressureControllerObj.GetComponent<PressureTrackerMain>() != null;
            ok &= Check(results, pressureControllerOk, label + " Pressure Controller present",
                "\"" + label + " Pressure Controller\" (with a PressureTrackerMain component) was not found in the scene -- every " + label.ToLowerInvariant() + "-hand HapticFingerTrigger will NullReferenceException the first time it tries to fire.");

            ok &= ValidateFingerHaptics(tracking, expected, label, results);

            return ok;
        }

        // Confirms every fingertip + palm joint on the raw tracked hand has both a trigger
        // Collider and a correctly configured HapticFingerTrigger -- what AutoAddFingerHaptics
        // is supposed to have wired up.
        private static bool ValidateFingerHaptics(HexRTrackedHand tracking, HaptGloveHandler.HandType expected, string label, List<SetupCheck> results)
        {
            if (tracking.handRoot == null) return true; // already reported by the handRoot check above

            bool ok = true;
            HapticFingerTrigger.HandType expectedTriggerHand = expected == HaptGloveHandler.HandType.Left ? HapticFingerTrigger.HandType.Left : HapticFingerTrigger.HandType.Right;

            foreach (HapticFingerTrigger.FingerType finger in HexRManager.Fingers)
            {
                Transform joint = tracking.ResolveRawFingerJoint(finger);
                ok &= CheckFingerHapticJoint(joint, finger, expectedTriggerHand, label, results);
            }

            ok &= CheckFingerHapticJoint(tracking.ResolveRawPalmJoint(), HapticFingerTrigger.FingerType.Palm, expectedTriggerHand, label, results);

            return ok;
        }

        private static bool CheckFingerHapticJoint(Transform joint, HapticFingerTrigger.FingerType finger, HapticFingerTrigger.HandType expectedTriggerHand, string label, List<SetupCheck> results)
        {
            string title = label + " " + finger + " haptic trigger";
            if (joint == null)
            {
                return Check(results, false, title, label + " " + finger + " joint could not be resolved on the raw hand -- run Auto Set Up HexR, or check the hand rig's joint naming.");
            }

            Collider collider = joint.GetComponent<Collider>();
            if (!Check(results, collider != null, title + " collider", label + " " + finger + " (" + joint.name + ") has no trigger Collider."))
            {
                return false;
            }

            // The check that would have caught collision haptics being silently dead: a trigger
            // collider with no Rigidbody on either side of the pair never raises an event, and
            // nothing else about the setup looks wrong when that happens.
            Rigidbody body = joint.GetComponent<Rigidbody>();
            string bodyProblem = null;
            if (body == null)
            {
                bodyProblem = "has no Rigidbody -- Unity raises no trigger events between two colliders that "
                    + "both lack one, so this finger will never fire haptics against a haptic zone";
            }
            else if (!body.isKinematic)
            {
                bodyProblem = "has a non-kinematic Rigidbody, so the fingertip will be simulated and drift off "
                    + "the tracked joint";
            }
            else if (body.useGravity)
            {
                bodyProblem = "has a Rigidbody with gravity enabled";
            }
            else if (body.collisionDetectionMode != CollisionDetectionMode.ContinuousSpeculative)
            {
                bodyProblem = "has a Rigidbody set to " + body.collisionDetectionMode + " rather than "
                    + "ContinuousSpeculative, so a fast finger can pass through a thin haptic zone between "
                    + "physics steps";
            }

            Check(results, bodyProblem == null, title + " kinematic Rigidbody",
                label + " " + finger + " (" + joint.name + ") " + bodyProblem
                + ". Re-run HexR > Troubleshoot > Re-run Auto Setup.");

            HapticFingerTrigger trigger = joint.GetComponent<HapticFingerTrigger>();
            if (!Check(results, trigger != null, title + " component", label + " " + finger + " (" + joint.name + ") has no HapticFingerTrigger."))
            {
                return false;
            }

            bool configOk = trigger.fingertype == finger && trigger.handType == expectedTriggerHand;
            return Check(results, configOk, title + " configured", label + " " + finger + " (" + joint.name + ")'s HapticFingerTrigger has the wrong fingertype/handType -- re-run Auto Set Up HexR.");
        }

        private static bool NameSuggestsHand(string name, HaptGloveHandler.HandType expected)
        {
            string n = name.ToLowerInvariant();
            bool looksLeft = n.StartsWith("l_") || n.Contains("_l") || n.Contains("left");
            bool looksRight = n.StartsWith("r_") || n.Contains("_r") || n.Contains("right");
            return expected == HaptGloveHandler.HandType.Left ? !looksRight : !looksLeft;
        }
    }
}
