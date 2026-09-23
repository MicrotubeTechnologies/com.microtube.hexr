using System.Collections.Generic;
using UnityEngine;

namespace HexR
{
    /// <summary>
    /// Adds HexR haptics to an ordinary interactable, whichever interaction SDK the project uses.
    ///
    /// This is the point of the integration: you build the app the ordinary way -- with
    /// XRGrabInteractable and XRSimpleInteractable on OpenXR, or Grabbable and HandGrabInteractable
    /// on Meta -- and then drop one of these alongside to make it felt. Nothing about the
    /// interactable changes, and none of HexR's own grab detection is involved: the SDK decides what
    /// is grabbed or pressed, HexR only decides what that should feel like.
    ///
    /// The component itself knows about neither SDK. It receives interaction state through
    /// <see cref="IHexRInteractionSink"/> from a backend adapter that lives in its own assembly
    /// (<c>HexR.Runtime.OpenXR</c> / <c>HexR.Runtime.MetaOVR</c>), each gated on its SDK being
    /// installed. That is why one component can serve both, and why this file compiles in a project
    /// with neither.
    ///
    /// Contrast with <see cref="HexRGrabbable"/>. That component runs its own physics-based grab off
    /// the glove's finger colliders and writes straight to the glove, bypassing
    /// <see cref="PressureTrackerMain"/> entirely. It works, but it means the object is grabbable
    /// *only* by a HexR hand -- so the app can't be built or tested without gloves, and it behaves
    /// differently from every other interactable in the scene. HexRGrabbable is deprecated and
    /// will be removed; this is the path for all new work.
    ///
    /// Haptics still only fire while <c>PressureTrackerMain.IsHandNear()</c> is true. The hand-near
    /// source on each Pressure Controller is what makes that true from the SDK's own grab and poke
    /// state, and the matching backend-setup class attaches it for you.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("HexR/Interactable Haptics")]
    public class HexRInteractableHaptics : MonoBehaviour, IHexRInteractionSink
    {
        public enum FireOn
        {
            /// <summary>Grabbed, or pressed.</summary>
            Select,

            /// <summary>Hand near enough for the SDK to highlight it.</summary>
            Hover,

            SelectAndHover,
        }

        [Header("When")]
        public FireOn fireOn = FireOn.Select;

        [Header("Which fingers")]
        public bool thumb = true;
        public bool index = true;
        public bool middle = true;
        public bool ring = false;
        public bool pinky = false;
        public bool palm = true;

        [Header("How hard")]
        [Tooltip("Pressure applied to each selected finger. The glove's usable range is 10 (barely " +
                 "there) to 60 (firm). 30 suits a small prop, 40 something heavier like a torch.")]
        [Range(0f, 60f)]
        public float strength = 30f;

        [Header("Follow the grip")]
        [Tooltip("Scale the pressure by how far the fingers are actually closed, instead of applying " +
                 "a fixed amount the moment the SDK reports a select. Squeeze harder, feel more. This " +
                 "is what gets the palm/pinch nuance back: the SDK has one binary select, but the " +
                 "glove's own finger tracking knows how closed each finger is.")]
        public bool followGrip = true;

        [Tooltip("Pressure at the lightest grip that still counts as holding.")]
        [Range(0f, 60f)]
        public float minStrength = 10f;

        [Header("Hands (optional)")]
        [Tooltip("Leave both empty -- they are found automatically. Set them to override the lookup, " +
                 "e.g. a scene with more than one rig where you want a specific pair; the Auto Find " +
                 "button below fills them with what the lookup would pick.")]
        public PressureTrackerMain leftPressureTracker;
        public PressureTrackerMain rightPressureTracker;

        [Header("Advanced")]
        [Tooltip("Which interaction system drives this. Auto reads the rig's HexR Manager > XR Framework, " +
                 "which is almost always what you want.")]
        public HexRBackendChoice backend = HexRBackendChoice.Auto;

        private const int Left = 0;
        private const int Right = 1;

        /// <summary>One interactor's current state. Absolute, never a running count of edges.</summary>
        private struct Engagement
        {
            public HexRHandSide hand;
            public bool hovering;
            public bool selecting;
        }

        private readonly Dictionary<object, Engagement> engagements = new Dictionary<object, Engagement>();

        // Per hand, indexed by Left/Right above. Driving both sides independently is what stops a
        // second hand stealing the first hand's pressure, which the single-tracker version did.
        private readonly PressureTrackerMain[] active = new PressureTrackerMain[2];
        private readonly FingerUseTracking[] fingers = new FingerUseTracking[2];
        private readonly float[] applied = { -1f, -1f };
        private readonly bool[] wanted = new bool[2];

        private IHexRInteractionBinding binding;
        private bool warnedNoTrackers;
        private bool warnedNoBackend;
        private bool warnedNothingToBind;
        private bool warnedUnknownHand;
        private bool warnedMissingSide;

        private void Start()
        {
            ResolveTrackers();
        }

        private void OnEnable()
        {
            Bind();
        }

        private void OnDisable()
        {
            if (binding != null)
            {
                binding.Dispose();
                binding = null;
            }

            engagements.Clear();
            ReleaseAll();
        }

        /// <summary>
        /// What is actually driving this object, for the Inspector and for diagnostics.
        /// </summary>
        public string BoundTo
        {
            get { return binding != null && binding.IsBound ? binding.Describe() : "nothing"; }
        }

        private void Bind()
        {
            string diagnostic;
            IHexRInteractionBackend resolved = HexRInteractionBackends.Resolve(backend, gameObject, out diagnostic);

            if (resolved == null)
            {
                binding = HexRInteractionBackends.Unbound;
                if (!warnedNoBackend)
                {
                    warnedNoBackend = true;
                    Debug.LogWarning("[HexR] " + name + ": " + diagnostic, this);
                }

                return;
            }

            binding = resolved.Bind(gameObject, this);

            if (!binding.IsBound && !warnedNothingToBind)
            {
                warnedNothingToBind = true;
                Debug.LogWarning("[HexR] " + name + ": " + resolved.DisplayName + " found no interactable on "
                                 + "this object, so there is nothing to attach haptics to.", this);
            }
        }

        /// <summary>
        /// Finds the two Pressure Controllers, so neither of these fields has to be dragged in.
        ///
        /// Tried in order of how much each can be trusted:
        /// <list type="number">
        /// <item>PressureTrackerMain.handType -- set by HexR's own Auto Setup, so it is the
        /// authoritative answer and survives any renaming of the rig;</item>
        /// <item>HexRManager.Instance.leftHand/rightHand -- exact, but needs the manager to have
        /// woken already, which it has by Start on a rig that lives in the scene;</item>
        /// <item>the object's name containing "Left"/"Right" -- the last resort, for a rig that
        /// predates handType being set.</item>
        /// </list>
        ///
        /// Anything already assigned in the Inspector is left alone, so an explicit override wins.
        ///
        /// Safe to call more than once: it only fills in what is still missing, which is what lets
        /// a grab retry the lookup if the rig had not finished loading at Start.
        /// </summary>
        public bool ResolveTrackers()
        {
            if (leftPressureTracker != null && rightPressureTracker != null)
            {
                return true;
            }

            PressureTrackerMain[] trackers = HexRCompat.FindAll<PressureTrackerMain>(true);

            // 1. handType -- but only when the rig actually disagrees about the two hands.
            //
            // The guard is not theoretical: a tutorial project shipped with both Pressure
            // Controllers set to Left, because the right one was duplicated from the left and the
            // field never updated. Trusting that blindly would have pointed both hands at the same
            // tracker and looked like a haptics bug rather than a data one.
            int lefts = 0, rights = 0;
            foreach (PressureTrackerMain t in trackers)
            {
                if (t.handType == PressureTrackerMain.HandType.Left) lefts++;
                else rights++;
            }

            if (lefts == 1 && rights == 1)
            {
                foreach (PressureTrackerMain t in trackers)
                {
                    if (leftPressureTracker == null && t.handType == PressureTrackerMain.HandType.Left)
                    {
                        leftPressureTracker = t;
                    }
                    else if (rightPressureTracker == null && t.handType == PressureTrackerMain.HandType.Right)
                    {
                        rightPressureTracker = t;
                    }
                }
            }

            // 2. through the manager, whose leftHand/rightHand sit on the Pressure Controllers
            if (HexRManager.Instance != null)
            {
                if (leftPressureTracker == null && HexRManager.Instance.leftHand != null)
                {
                    leftPressureTracker = HexRManager.Instance.leftHand.GetComponent<PressureTrackerMain>();
                }

                if (rightPressureTracker == null && HexRManager.Instance.rightHand != null)
                {
                    rightPressureTracker = HexRManager.Instance.rightHand.GetComponent<PressureTrackerMain>();
                }
            }

            // 3. by side in the name
            foreach (PressureTrackerMain t in trackers)
            {
                string n = t.gameObject.name;
                if (leftPressureTracker == null &&
                    n.IndexOf("Left", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    leftPressureTracker = t;
                }
                else if (rightPressureTracker == null &&
                         n.IndexOf("Right", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    rightPressureTracker = t;
                }
            }

            bool any = leftPressureTracker != null || rightPressureTracker != null;
            if (!any && !warnedNoTrackers)
            {
                warnedNoTrackers = true;
                Debug.LogWarning("[HexR] " + name + ": found no Pressure Controllers, so this object will be "
                                 + "grabbable but will not be felt. Run HexR > Troubleshoot > Re-run Auto Setup.", this);
            }

            return any;
        }

        void IHexRInteractionSink.SetInteractorState(object interactorKey, HexRHandSide hand,
                                                     bool hovering, bool selecting)
        {
            if (interactorKey == null)
            {
                return;
            }

            if (!hovering && !selecting)
            {
                engagements.Remove(interactorKey);
            }
            else
            {
                Engagement e;
                e.hand = hand;
                e.hovering = hovering;
                e.selecting = selecting;
                engagements[interactorKey] = e;
            }

            Recompute();
        }

        void IHexRInteractionSink.ClearInteractor(object interactorKey)
        {
            if (interactorKey != null && engagements.Remove(interactorKey))
            {
                Recompute();
            }
        }

        /// <summary>
        /// Whether an engagement in this state should be felt.
        ///
        /// <see cref="FireOn.Hover"/> ORs in <c>selecting</c> as a safety net rather than as a
        /// feature: on both current backends hover is retained through a select, so this changes
        /// nothing today, but an SDK that dropped hover the moment it selected would otherwise make
        /// the object go quiet at exactly the moment it was grabbed.
        /// </summary>
        private bool Wants(Engagement e)
        {
            return fireOn == FireOn.Select ? e.selecting : e.hovering || e.selecting;
        }

        /// <summary>
        /// Derive both hands' pressure from the engagements currently held.
        ///
        /// Deriving rather than reacting to edges is what makes the two backends behave the same:
        /// XRI and Meta do not agree on the order of hover and select callbacks, and the previous
        /// edge-driven version could be left holding pressure, or drop it while still grabbed,
        /// depending on that order.
        /// </summary>
        private void Recompute()
        {
            wanted[Left] = false;
            wanted[Right] = false;

            foreach (KeyValuePair<object, Engagement> pair in engagements)
            {
                if (!Wants(pair.Value))
                {
                    continue;
                }

                int side = SideFor(pair.Value.hand);
                if (side >= 0)
                {
                    wanted[side] = true;
                }
            }

            for (int side = Left; side <= Right; side++)
            {
                if (wanted[side])
                {
                    Engage(side);
                }
                else
                {
                    Disengage(side);
                }
            }
        }

        /// <summary>
        /// Which Pressure Controller a hand maps onto, or -1 for none.
        ///
        /// A known side with no tracker returns -1 rather than falling through to the other hand:
        /// buzzing the wrong glove is worse than buzzing neither, and it hides the setup mistake.
        /// An unknown side does fall back, because that is the older behaviour and a single-glove
        /// rig genuinely has only one answer.
        /// </summary>
        private int SideFor(HexRHandSide hand)
        {
            // Retry here rather than only at Start: on a rig that finishes loading late -- or after
            // a scene load, where the Pressure Controllers ride the persistent rig and this object
            // does not -- Start can run before there is anything to find.
            if (leftPressureTracker == null || rightPressureTracker == null)
            {
                ResolveTrackers();
            }

            switch (hand)
            {
                case HexRHandSide.Left:
                    if (leftPressureTracker != null)
                    {
                        return Left;
                    }

                    WarnMissingSide("left");
                    return -1;

                case HexRHandSide.Right:
                    if (rightPressureTracker != null)
                    {
                        return Right;
                    }

                    WarnMissingSide("right");
                    return -1;

                default:
                    if (!warnedUnknownHand)
                    {
                        warnedUnknownHand = true;
                        Debug.LogWarning("[HexR] " + name + ": could not tell which hand is interacting, so "
                                         + "haptics are going to whichever glove is available. Check that the "
                                         + "interactors are under the HexR hand roots.", this);
                    }

                    if (rightPressureTracker != null) return Right;
                    if (leftPressureTracker != null) return Left;
                    return -1;
            }
        }

        private void WarnMissingSide(string side)
        {
            if (warnedMissingSide)
            {
                return;
            }

            warnedMissingSide = true;
            Debug.LogWarning("[HexR] " + name + ": the " + side + " hand interacted but there is no " + side
                             + " Pressure Controller, so nothing was felt. Run HexR > Troubleshoot > Re-run Auto Setup.", this);
        }

        private void Engage(int side)
        {
            PressureTrackerMain tracker = side == Left ? leftPressureTracker : rightPressureTracker;
            if (tracker == null)
            {
                return;
            }

            if (!ReferenceEquals(active[side], tracker))
            {
                active[side] = tracker;
                fingers[side] = tracker.GetComponent<FingerUseTracking>();
                applied[side] = -1f;
            }

            Push(side, followGrip ? PressureFromGrip(side) : strength);
        }

        private void Disengage(int side)
        {
            if (active[side] == null)
            {
                return;
            }

            RemoveAll(side);
            active[side] = null;
            fingers[side] = null;
            applied[side] = -1f;
        }

        private void Update()
        {
            if (!followGrip)
            {
                return;
            }

            for (int side = Left; side <= Right; side++)
            {
                if (active[side] != null)
                {
                    Push(side, PressureFromGrip(side));
                }
            }
        }

        /// <summary>
        /// How closed are the fingers this object cares about, mapped onto the pressure range.
        ///
        /// FingerUseTracking normalises each finger's tip-to-knuckle distance to 1 when extended
        /// and 0 when curled, so the grip is one minus that. It calibrates itself from the widest
        /// and narrowest it has seen, which means the numbers are meaningless until the hand has
        /// opened and closed once -- so an uncalibrated hand falls back to the fixed strength
        /// rather than reporting a fully open hand and dropping the pressure to nothing.
        /// </summary>
        private float PressureFromGrip(int side)
        {
            // Re-checked every call rather than cached: FingerUseTracking's joint references are
            // scene-local, so across a scene load the component can survive while the thing it
            // reads does not.
            FingerUseTracking use = fingers[side];
            if (use == null)
            {
                return strength;
            }

            float sum = 0f;
            int n = 0;
            if (thumb) { sum += use.ThumbUse; n++; }
            if (index) { sum += use.IndexUse; n++; }
            if (middle) { sum += use.MiddleUse; n++; }
            if (ring) { sum += use.RingUse; n++; }
            if (pinky) { sum += use.LittleUse; n++; }

            if (n == 0)
            {
                return strength;
            }

            float openness = sum / n;
            if (openness <= 0f)
            {
                return strength;            // not calibrated yet
            }

            float grip = Mathf.Clamp01(1f - openness);
            return Mathf.Lerp(minStrength, strength, grip);
        }

        /// <summary>
        /// Sends a pressure, but only when it has actually changed. The glove quantises pressure to
        /// steps of 10 -- HexRGrabbable rounds to the same grid -- so following the grip literally
        /// every frame would be dozens of identical Bluetooth writes a second for no perceptible
        /// gain. With both hands now drivable at once, that throttle matters twice as much.
        /// </summary>
        private void Push(int side, float pressure)
        {
            PressureTrackerMain tracker = active[side];
            if (tracker == null)
            {
                return;
            }

            float stepped = Mathf.Round(Mathf.Clamp(pressure, 0f, 60f) / 10f) * 10f;
            if (Mathf.Approximately(stepped, applied[side]))
            {
                return;
            }

            applied[side] = stepped;

            if (stepped <= 0f)
            {
                RemoveAll(side);
                return;
            }

            if (thumb) tracker.SingleThumbHaptic(stepped);
            if (index) tracker.SingleIndexHaptic(stepped);
            if (middle) tracker.SingleMiddleHaptic(stepped);
            if (ring) tracker.SingleRingHaptic(stepped);
            if (pinky) tracker.SinglePinkyHaptic(stepped);
            if (palm) tracker.SinglePalmHaptic(stepped);
        }

        private void RemoveAll(int side)
        {
            PressureTrackerMain tracker = active[side];
            if (tracker == null)
            {
                return;
            }

            if (thumb) tracker.RemoveThumbHaptics();
            if (index) tracker.RemoveIndexHaptics();
            if (middle) tracker.RemoveMiddleHaptics();
            if (ring) tracker.RemoveRingHaptics();
            if (pinky) tracker.RemovePinkyHaptics();
            if (palm) tracker.RemovePalmHaptics();
        }

        private void ReleaseAll()
        {
            for (int side = Left; side <= Right; side++)
            {
                Disengage(side);
            }
        }
    }
}
