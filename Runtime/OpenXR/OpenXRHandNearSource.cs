using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace HexR.OpenXR
{
    /// <summary>
    /// Feeds the XR Interaction Toolkit's grab and poke state into a <see cref="PressureTrackerMain"/>'s
    /// hand-near gating -- the OpenXR counterpart of <see cref="HexR.MetaOVR.MetaOVRHandNearSource"/>.
    ///
    /// Why this exists. Haptics only fire when <c>PressureTrackerMain.IsHandNear()</c> is true, and
    /// that ORs three flags: HandGrabbing, PokeHovering and CollisionNearHand. On Meta the first
    /// two are filled in by MetaOVRHandNearSource reading the Interaction SDK's interactors. On
    /// OpenXR nothing filled them in at all, so the only way to make a hand "near" was to put a
    /// ProximityCheck volume on every object -- and forgetting one produced silence that looks
    /// exactly like a dead glove. This closes that gap: XRI already knows when a hand is grabbing
    /// or hovering something, so ask it.
    ///
    /// The result is that an OpenXR project is built the ordinary way -- XRGrabInteractable,
    /// XRSimpleInteractable, the hand interactors XRI ships -- and HexR reads that existing
    /// interaction state rather than asking you to model proximity a second time. ProximityCheck
    /// is still the right tool for a haptic *zone* you reach into, which is not an interactable
    /// and has no interactor state to read.
    ///
    /// You should not need to add this by hand: <see cref="OpenXRBackendSetup"/> attaches it to
    /// every Pressure Controller, exactly as the Meta backend does for its own.
    ///
    /// It reuses PressureTrackerMain's handGrabInteractor/pokeInteractor fields, which are typed
    /// MonoBehaviour precisely so either backend can put its own interactor type in them.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PressureTrackerMain))]
    public class OpenXRHandNearSource : MonoBehaviour
    {
        [Tooltip("Where to look for this hand's interactors. Leave empty to search up from the hand " +
                 "this Pressure Controller belongs to.")]
        public Transform searchRoot;

        [Tooltip("Treat hovering a grabbable as 'hand near', not just actually holding it. Usually " +
                 "what you want: the glove should respond as the hand closes on an object.")]
        public bool grabHoverCountsAsNear = true;

        private PressureTrackerMain tracker;

        // Cast once and cached, re-resolved only when the underlying reference changes -- casting
        // both interactors every frame on both hands is pure waste.
        private MonoBehaviour cachedGrabSource;
        private MonoBehaviour cachedPokeSource;
        private XRBaseInteractor grabInteractor;
        private XRBaseInteractor pokeInteractor;

        private void Awake()
        {
            tracker = GetComponent<PressureTrackerMain>();
        }

        private void Start()
        {
            if (tracker == null || (tracker.handGrabInteractor != null && tracker.pokeInteractor != null))
            {
                return;
            }

            Transform root = ResolveSearchRoot();
            if (root == null)
            {
                Debug.LogWarning("[HexR] " + name + ": no hand root to search for XRI interactors, so grab and " +
                                 "poke gating stays off for this hand. Assign searchRoot, or set " +
                                 "handGrabInteractor/pokeInteractor on the Pressure Controller by hand.");
                return;
            }

            AutoFind(root, name);
        }

        private Transform ResolveSearchRoot()
        {
            Transform start = searchRoot;
            if (start == null)
            {
                HexRTrackedHand tracking = GetComponentInParent<HexRTrackedHand>();
                start = tracking != null ? tracking.handRoot : null;
            }

            // The Pressure Controllers are not under the hands in either rig prefab, so the
            // lookup above finds nothing for them. Go through the rig to this side's hand instead.
            if (start == null && tracker != null)
            {
                start = tracker.TrackedHandRoot;
            }

            return ClimbToInteractors(start);
        }

        /// <summary>
        /// The nearest ancestor of <paramref name="from"/> (itself included) whose subtree holds an
        /// XRI interactor. XRI's interactors are siblings of the tracked hand visual rather than
        /// children of it -- Left Hand/{Left Hand Interaction Visual/L_Wrist, Direct Interactor, ...}
        /// -- so starting at the wrist and searching down finds nothing; walking up is what reaches
        /// them. Applied to an assigned searchRoot too, so one set to the wrist still works.
        /// </summary>
        public static Transform ClimbToInteractors(Transform from)
        {
            for (Transform t = from; t != null; t = t.parent)
            {
                if (t.GetComponentInChildren<XRBaseInteractor>(true) != null)
                {
                    return t;
                }
            }

            return null;
        }

        /// <summary>
        /// Assigns whichever interactors aren't already set. Inactive children are searched too,
        /// because the rig keeps interactors disabled until the hand is tracked.
        /// </summary>
        /// <param name="handRoot">Subtree to search.</param>
        /// <param name="label">Name used in warnings, so a failure says which hand it was.</param>
        public bool AutoFind(Transform handRoot, string label = null)
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
                // XRI 3's hand rig (XR Origin Hands) grabs with a Near-Far Interactor and has no
                // Direct Interactor at all. Until 0.9.4 only the Direct one was looked for, so on
                // that rig grab gating stayed off and grabs went unfelt.
                XRBaseInteractor grab = handRoot.GetComponentInChildren<XRDirectInteractor>(true);
                if (grab == null)
                {
                    grab = handRoot.GetComponentInChildren<NearFarInteractor>(true);
                }
                tracker.handGrabInteractor = grab;
            }

            if (tracker.pokeInteractor == null)
            {
                tracker.pokeInteractor = handRoot.GetComponentInChildren<XRPokeInteractor>(true);
            }

            if (tracker.handGrabInteractor == null || tracker.pokeInteractor == null)
            {
                Debug.LogWarning("[HexR] " + (label ?? name) + ": couldn't find a grab interactor (XR Direct or " +
                                 "Near-Far) and an XR Poke Interactor under \"" + handRoot.name + "\". Grab or " +
                                 "poke gating will stay off for this hand.");
                return false;
            }

            return true;
        }

        private void Update()
        {
            if (tracker == null)
            {
                return;
            }

            Resolve();

            bool grabbing = IsGrabbingNear();
            bool poking = pokeInteractor != null && pokeInteractor.hasHover;

            tracker.HandGrabbingCheck(grabbing);
            tracker.PokeHoveringCheck(poking);
        }

        /// <summary>
        /// Whether the grab interactor has something in the hand, or within reach of it.
        ///
        /// A Near-Far Interactor also selects and hovers from across the room along its ray, and
        /// the glove should not press for an object the hand is nowhere near. So for one of those
        /// only a near selection counts, and hover counts only when far casting is switched off
        /// (then everything it hovers is in reach). A Direct Interactor only ever reaches what is
        /// at the hand, so all of its state counts, as before.
        /// </summary>
        private bool IsGrabbingNear()
        {
            if (grabInteractor == null)
            {
                return false;
            }

            NearFarInteractor nearFar = grabInteractor as NearFarInteractor;
            if (nearFar != null)
            {
                bool nearSelect = nearFar.hasSelection
                                  && nearFar.selectionRegion.Value == NearFarInteractor.Region.Near;
                bool nearHover = grabHoverCountsAsNear && !nearFar.enableFarCasting && nearFar.hasHover;
                return nearSelect || nearHover;
            }

            return grabInteractor.hasSelection || (grabHoverCountsAsNear && grabInteractor.hasHover);
        }

        private void Resolve()
        {
            // Compared with Unity's == rather than ReferenceEquals on purpose. A scene load leaves
            // the tracker (which rides the DontDestroyOnLoad rig) holding a destroyed interactor
            // from the previous scene. ReferenceEquals sees the same managed object and skips the
            // re-cast, so the stale reference is never replaced and gating goes quiet for the rest
            // of the session -- silently, because the null guard below then reads it as null.
            if (cachedGrabSource != tracker.handGrabInteractor || (grabInteractor == null && tracker.handGrabInteractor != null))
            {
                cachedGrabSource = tracker.handGrabInteractor;
                grabInteractor = cachedGrabSource as XRBaseInteractor;
                WarnOnMismatch(cachedGrabSource, grabInteractor, "handGrabInteractor");
            }

            if (cachedPokeSource != tracker.pokeInteractor || (pokeInteractor == null && tracker.pokeInteractor != null))
            {
                cachedPokeSource = tracker.pokeInteractor;
                pokeInteractor = cachedPokeSource as XRBaseInteractor;
                WarnOnMismatch(cachedPokeSource, pokeInteractor, "pokeInteractor");
            }
        }

        // The fields accept any MonoBehaviour, so a wrong drag in the Inspector would otherwise fail
        // completely silently: the cast yields null and gating just stays off.
        private void WarnOnMismatch(MonoBehaviour assigned, Object resolved, string fieldName)
        {
            if (assigned != null && resolved == null)
            {
                Debug.LogWarning("[HexR] " + name + ": " + fieldName + " is a " + assigned.GetType().Name +
                                 ", which isn't an XRBaseInteractor. That half of the gating stays off.");
            }
        }
    }
}
