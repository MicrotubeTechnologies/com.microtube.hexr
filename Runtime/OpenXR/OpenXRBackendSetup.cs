using UnityEngine;
using UnityEngine.SceneManagement;

namespace HexR.OpenXR
{
    /// <summary>
    /// Wires the OpenXR half of the hand-near gating, from both ends -- the mirror image of
    /// <see cref="HexR.MetaOVR.MetaOVRBackendSetup"/>:
    /// <list type="bullet">
    /// <item>at edit time, by registering into <see cref="HexRManager.PressureTrackerBackendSetup"/>
    /// so AutoSetup attaches and fills in <see cref="OpenXRHandNearSource"/>;</item>
    /// <item>at run time, by attaching that component to any Pressure Controller that was wired
    /// before it existed.</item>
    /// </list>
    ///
    /// The registration is the whole point of the first half: HexR.Runtime can't reference this
    /// assembly (that would drag the XR Interaction Toolkit back into the core and undo the split),
    /// so the dependency runs the other way and this side announces itself. When XRI isn't
    /// installed, HEXR_XRI is undefined, this assembly is excluded, and nothing registers.
    ///
    /// Why this exists at all: without it, an OpenXR project has to place a hand-near source on
    /// every Pressure Controller by hand, while a Meta project places nothing. That asymmetry is
    /// not a platform difference, it is a missing half of the package -- and it is what pushed
    /// OpenXR tutorial projects into carrying their own copies of integration code.
    /// </summary>
    public static class OpenXRBackendSetup
    {
        // AutoSetup runs from the Editor (menu item / Inspector button), so the editor-load hook
        // is the one that matters. The runtime hook covers a build that calls AutoSetup itself.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        private static void Register()
        {
            // Domain reloads re-run this, and both attributes can fire in the same session --
            // unsubscribe first so the wiring can't be invoked twice per AutoSetup.
            HexRManager.PressureTrackerBackendSetup -= Wire;
            HexRManager.PressureTrackerBackendSetup += Wire;

            // Announce the interaction adapter too, so HexRInteractableHaptics can be driven by
            // XRI without HexR.Runtime referencing it. Register is idempotent, so the repeat from
            // a domain reload is harmless.
            HexRInteractionBackends.Register(new OpenXRInteractionBackend());
        }

        private static void Wire(PressureTrackerMain tracker, Transform handRoot, string label)
        {
            if (tracker == null || !IsOpenXRRig(tracker))
            {
                return;
            }

            OpenXRHandNearSource source = Attach(tracker);
            source.searchRoot = handRoot;
            source.AutoFind(handRoot, label);

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(source);
            UnityEditor.EditorUtility.SetDirty(tracker);
#endif
        }

        private static OpenXRHandNearSource Attach(PressureTrackerMain tracker)
        {
            OpenXRHandNearSource source = tracker.GetComponent<OpenXRHandNearSource>();
            return source != null ? source : tracker.gameObject.AddComponent<OpenXRHandNearSource>();
        }

        /// <summary>
        /// Whether this Pressure Controller belongs to an OpenXR rig.
        ///
        /// Only matters in a project that has both the Meta SDK and XRI installed, where both
        /// backend assemblies compile and both subscribe to the same hook. Without this, whichever
        /// rig AutoSetup touched would get both hand-near sources attached.
        ///
        /// Falls back to true when no manager can be found: the Meta side asks the same question
        /// and answers false, so an unattributable tracker ends up with the OpenXR wiring rather
        /// than none at all -- and OpenXR is the framework that works without a vendor SDK.
        /// </summary>
        private static bool IsOpenXRRig(PressureTrackerMain tracker)
        {
            HexRManager manager = tracker.GetComponentInParent<HexRManager>();
            if (manager == null)
            {
                manager = HexRManager.Instance;
            }

            return manager == null || manager.XRFramework == HexRManager.Options.OpenXR;
        }

        // Attach to every OpenXR Pressure Controller at load, so a scene that was never run
        // through Auto Setup still gets its grab and poke gating.
        //
        // Deliberately NOT gated on the interactors already being assigned, which is the test the
        // Meta side uses. On Meta those references are serialised by Auto Setup, so they are
        // present by the time this runs; on OpenXR OpenXRHandNearSource resolves them itself in
        // Start, so at AfterSceneLoad they are still null and that test would never pass. Asking
        // "is this an OpenXR rig" instead is both the question actually being asked and the one
        // that keeps this off a Meta rig in a project that has XRI installed too.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AttachToExistingScenes()
        {
            SceneManager.sceneLoaded += (scene, mode) => AttachAll();
            AttachAll();
        }

        private static void AttachAll()
        {
            PressureTrackerMain[] trackers = HexRCompat.FindAll<PressureTrackerMain>(true);

            foreach (PressureTrackerMain tracker in trackers)
            {
                if (IsOpenXRRig(tracker) && tracker.GetComponent<OpenXRHandNearSource>() == null)
                {
                    Attach(tracker);
                }
            }
        }
    }
}
