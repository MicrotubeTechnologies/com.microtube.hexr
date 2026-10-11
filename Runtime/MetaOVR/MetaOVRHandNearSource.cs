using UnityEngine;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;

namespace HexR.MetaOVR
{
    /// <summary>
    /// Feeds Meta OVR's grab/poke state into a <see cref="PressureTrackerMain"/>'s hand-near
    /// gating.
    ///
    /// PressureTrackerMain used to poll the interactors itself, which meant the core runtime
    /// assembly referenced Oculus.Interaction and so could not be compiled -- or even installed
    /// -- in a project without the Meta SDK. Only the polling moved here; the two interactor
    /// fields stay on PressureTrackerMain (widened to MonoBehaviour) because they hold live
    /// scene references that relocating the field would silently drop.
    ///
    /// This whole assembly is gated on HEXR_META_OVR, so in an OpenXR project it doesn't exist
    /// and nothing here runs. The OpenXR path leaves HandGrabbing/PokeHovering false and relies
    /// on ProximityCheck for CollisionNearHand instead.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PressureTrackerMain))]
    public class MetaOVRHandNearSource : MonoBehaviour
    {
        [Tooltip("Where to search when auto-finding the interactors. Defaults to this hand's HexRTrackedHand.handRoot.")]
        public Transform searchRoot;

        private PressureTrackerMain tracker;

        // Cast once and cached: the fields are typed MonoBehaviour on the tracker, and casting
        // both of them every frame on both hands is pure waste. Re-resolved whenever the
        // underlying reference changes, so wiring them in the Inspector during Play mode works.
        private MonoBehaviour cachedGrabSource;
        private MonoBehaviour cachedPokeSource;
        private HandGrabInteractor grabInteractor;
        private PokeInteractor pokeInteractor;

        private void Awake()
        {
            tracker = GetComponent<PressureTrackerMain>();
        }

        private void Start()
        {
            // Self-heal for rigs assembled by hand rather than through AutoSetup, and for scenes
            // saved before this component existed. Only searches for whichever is missing.
            if (tracker == null || (tracker.handGrabInteractor != null && tracker.pokeInteractor != null))
            {
                return;
            }

            Transform root = ResolveSearchRoot();
            if (root == null)
            {
                Debug.LogWarning("[HexR] " + name + ": no hand root to search for Meta interactors -- grab/poke "
                    + "haptics gating will stay off for this hand. Assign handGrabInteractor/pokeInteractor on the "
                    + "Pressure Controller, set searchRoot, or re-run HexR > Troubleshoot > Re-run Auto Setup.");
                return;
            }

            AutoFind(root, name);
        }

        private Transform ResolveSearchRoot()
        {
            if (searchRoot != null)
            {
                return searchRoot;
            }

            // The hand's own HexRTrackedHand is the reliable way to reach the tracked hand root --
            // the same one AutoSetup hands to the backend hook. The Pressure Controller is not
            // under the hand in the rig prefab, so if searching up finds nothing, go through the rig.
            HexRTrackedHand tracking = GetComponentInParent<HexRTrackedHand>();
            if (tracking != null && tracking.handRoot != null)
            {
                return tracking.handRoot;
            }

            return tracker != null ? tracker.TrackedHandRoot : null;
        }

        /// <summary>
        /// Finds and assigns whichever interactors aren't already set on the tracker, searching
        /// inactive children too (the OVR rig keeps interactors disabled until the hand is
        /// tracked). Returns true only if both ended up assigned.
        /// </summary>
        public bool AutoFind(Transform handRoot, string label)
        {
            if (tracker == null)
            {
                tracker = GetComponent<PressureTrackerMain>();
            }
            if (tracker == null || handRoot == null)
            {
                return false;
            }

            if (tracker.handGrabInteractor == null)
            {
                tracker.handGrabInteractor = FindForThisHand<HandGrabInteractor>(handRoot);
            }
            if (tracker.pokeInteractor == null)
            {
                tracker.pokeInteractor = FindForThisHand<PokeInteractor>(handRoot);
            }

            if (tracker.handGrabInteractor == null || tracker.pokeInteractor == null)
            {
                Debug.LogWarning("[HexR] AutoSetup: couldn't auto-find " + label + " Pressure Controller's "
                    + "HandGrabInteractor/PokeInteractor for the " + tracker.handType + " hand, under \"" + handRoot.name
                    + "\" or beside it -- manual wiring may be needed for Meta OVR grab/poke-based haptics gating.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// The interactor of type <typeparamref name="T"/> that belongs to this Pressure
        /// Controller's hand.
        ///
        /// Under the tracked hand first, which is where older rigs keep them. Meta's Interactions
        /// Rig keeps them in their own branch beside the hands instead (LeftInteractions /
        /// RightInteractions), and until 0.9.4 that was never looked at, so Auto Setup reported it
        /// couldn't auto-find them. So climb from the hand one level at a time, and at each level
        /// take an interactor that is clearly this hand's: by the hand it is bound to once it has
        /// woken, otherwise by the nearest Left/Right in its path. One that cannot be placed is
        /// never taken -- pressing the wrong glove is worse than pressing neither. Each side of the
        /// Interactions Rig also carries controller versions of both interactors
        /// (ControllerHandGrabInteractor, ControllerPokeInteractor), so the hand-tracked one is
        /// preferred when both are there.
        /// </summary>
        private T FindForThisHand<T>(Transform handRoot) where T : Component
        {
            T underHand = handRoot.GetComponentInChildren<T>(true);
            if (underHand != null)
            {
                return underHand;
            }

            bool wantLeft = tracker.handType == PressureTrackerMain.HandType.Left;
            for (Transform level = handRoot.parent; level != null; level = level.parent)
            {
                T controllerOne = null;
                foreach (T candidate in level.GetComponentsInChildren<T>(true))
                {
                    int side = SideOf(candidate);
                    if (side == 0 || (side < 0) != wantLeft)
                    {
                        continue;
                    }

                    if (!IsControllerDriven(candidate.transform, level))
                    {
                        return candidate;
                    }

                    if (controllerOne == null)
                    {
                        controllerOne = candidate;
                    }
                }

                if (controllerOne != null)
                {
                    return controllerOne;
                }
            }

            return null;
        }

        private static bool IsControllerDriven(Transform interactor, Transform stopAt)
        {
            for (Transform t = interactor; t != null && t != stopAt; t = t.parent)
            {
                if (t.name.IndexOf("Controller", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        // -1 for left, 1 for right, 0 when it cannot be told.
        private static int SideOf(Component interactor)
        {
            HandGrabInteractor grab = interactor as HandGrabInteractor;
            if (grab != null && grab.Hand != null)
            {
                return grab.Hand.Handedness == Oculus.Interaction.Input.Handedness.Left ? -1 : 1;
            }

            for (Transform t = interactor.transform; t != null; t = t.parent)
            {
                bool left = t.name.IndexOf("Left", System.StringComparison.OrdinalIgnoreCase) >= 0;
                bool right = t.name.IndexOf("Right", System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (left != right)
                {
                    return left ? -1 : 1;
                }
            }

            return 0;
        }

        private void Update()
        {
            if (tracker == null)
            {
                return;
            }

            Resolve();

            // Same semantics PressureTrackerMain.IsHandGrabbing/IsPokeHover had: an unassigned
            // (or wrong-typed) interactor reads as "not grabbing/poking" rather than throwing.
            tracker.HandGrabbingCheck(grabInteractor != null && grabInteractor.HasInteractable);
            tracker.PokeHoveringCheck(pokeInteractor != null && pokeInteractor.HasInteractable);
        }

        private void Resolve()
        {
            if (!ReferenceEquals(cachedGrabSource, tracker.handGrabInteractor))
            {
                cachedGrabSource = tracker.handGrabInteractor;
                grabInteractor = cachedGrabSource as HandGrabInteractor;
                WarnOnMismatch(cachedGrabSource, grabInteractor, "handGrabInteractor", "HandGrabInteractor");
            }

            if (!ReferenceEquals(cachedPokeSource, tracker.pokeInteractor))
            {
                cachedPokeSource = tracker.pokeInteractor;
                pokeInteractor = cachedPokeSource as PokeInteractor;
                WarnOnMismatch(cachedPokeSource, pokeInteractor, "pokeInteractor", "PokeInteractor");
            }
        }

        // The fields accept any MonoBehaviour now, so a wrong drag in the Inspector would
        // otherwise fail completely silently -- the cast just yields null and gating stays off.
        private void WarnOnMismatch(MonoBehaviour assigned, Object resolved, string fieldName, string expectedType)
        {
            if (assigned != null && resolved == null)
            {
                Debug.LogWarning("[HexR] " + name + ": " + fieldName + " is assigned a "
                    + assigned.GetType().Name + ", which is not a " + expectedType
                    + " -- grab/poke gating will stay off for this hand.");
            }
        }
    }
}
