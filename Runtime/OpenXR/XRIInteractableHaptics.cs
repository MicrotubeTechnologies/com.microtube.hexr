using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace HexR.OpenXR
{
    /// <summary>
    /// Adds HexR haptics to a stock XR Interaction Toolkit interactable.
    ///
    /// This is the whole point of the OpenXR integration: you build the app the ordinary way, with
    /// XRGrabInteractable and XRSimpleInteractable and the hand interactors XRI ships, and then
    /// drop one of these alongside to make it felt. Nothing about the interactable changes, and
    /// none of HexR's own grab detection is involved -- XRI decides what is grabbed or pressed,
    /// HexR only decides what that should feel like.
    ///
    /// Contrast with <see cref="HexRGrabbable"/>. That component runs its own physics-based grab
    /// off the glove's finger colliders and writes straight to the glove, bypassing
    /// <see cref="PressureTrackerMain"/> entirely. It works, but it means the object is grabbable
    /// *only* by a HexR hand -- so the app can't be built or tested without gloves, and it behaves
    /// differently from every other interactable in the scene. Both are supported; this one is the
    /// right default for a project already using XRI.
    ///
    /// Haptics still only fire while <c>PressureTrackerMain.IsHandNear()</c> is true.
    /// <see cref="OpenXRHandNearSource"/> on each Pressure Controller is what makes that true from
    /// XRI's own grab and poke state, and <see cref="OpenXRBackendSetup"/> attaches it for you.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("HexR/XRI Interactable Haptics")]
    public class XRIInteractableHaptics : MonoBehaviour
    {
        public enum FireOn
        {
            /// <summary>Grabbed, or pressed.</summary>
            Select,

            /// <summary>Hand near enough for XRI to highlight it.</summary>
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
                 "there) to 60 (firm). HexRGrabbable used 30 for small props and 40 for a torch.")]
        [Range(0f, 60f)]
        public float strength = 30f;

        [Header("Follow the grip")]
        [Tooltip("Scale the pressure by how far the fingers are actually closed, instead of applying " +
                 "a fixed amount the moment XRI reports a select. Squeeze harder, feel more. This is " +
                 "what gets the palm/pinch nuance back: XRI has one binary select, but the glove's " +
                 "own finger tracking knows how closed each finger is.")]
        public bool followGrip = true;

        [Tooltip("Pressure at the lightest grip that still counts as holding.")]
        [Range(0f, 60f)]
        public float minStrength = 10f;

        [Header("Hands")]
        [Tooltip("Left and right Pressure Controllers. Found by side at Start when left empty.")]
        public PressureTrackerMain leftPressureTracker;
        public PressureTrackerMain rightPressureTracker;

        private XRBaseInteractable interactable;
        private PressureTrackerMain activeTracker;
        private FingerUseTracking activeFingers;
        private float appliedPressure = -1f;

        private void Awake()
        {
            interactable = GetComponent<XRBaseInteractable>();
            if (interactable == null)
            {
                Debug.LogWarning("[HexR] " + name + ": no XR interactable on this object, so there is nothing " +
                                 "to attach haptics to. Add an XRGrabInteractable or XRSimpleInteractable.");
            }
        }

        private void Start()
        {
            // Find the two Pressure Controllers by which hand they sit on rather than by an exact
            // object name. They are named "Left/Right Pressure Controller", which is what the
            // package's own lookups require, but matching on the side keeps this working on a rig
            // someone has renamed -- which has happened, and it broke haptics everywhere.
            if (leftPressureTracker == null || rightPressureTracker == null)
            {
                PressureTrackerMain[] trackers = HexRCompat.FindAll<PressureTrackerMain>(true);
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
            }

            if (leftPressureTracker == null && rightPressureTracker == null)
            {
                Debug.LogWarning("[HexR] " + name + ": found no Pressure Controllers, so this object will be " +
                                 "grabbable but will not be felt. Run HexR > Auto Setup Scene.");
            }
        }

        private void OnEnable()
        {
            if (interactable == null)
            {
                return;
            }

            if (fireOn != FireOn.Hover)
            {
                interactable.selectEntered.AddListener(OnSelectEntered);
                interactable.selectExited.AddListener(OnSelectExited);
            }

            if (fireOn != FireOn.Select)
            {
                interactable.hoverEntered.AddListener(OnHoverEntered);
                interactable.hoverExited.AddListener(OnHoverExited);
            }
        }

        private void OnDisable()
        {
            if (interactable == null)
            {
                return;
            }

            interactable.selectEntered.RemoveListener(OnSelectEntered);
            interactable.selectExited.RemoveListener(OnSelectExited);
            interactable.hoverEntered.RemoveListener(OnHoverEntered);
            interactable.hoverExited.RemoveListener(OnHoverExited);
            Release();
        }

        private void OnSelectEntered(SelectEnterEventArgs args) => Apply(TrackerFor(args.interactorObject as MonoBehaviour));

        private void OnSelectExited(SelectExitEventArgs args) => Release();

        private void OnHoverEntered(HoverEnterEventArgs args) => Apply(TrackerFor(args.interactorObject as MonoBehaviour));

        private void OnHoverExited(HoverExitEventArgs args) => Release();

        /// <summary>
        /// Which hand did this. XRI tells us the interactor, and on the rig the interactors sit
        /// under objects named "Left Hand"/"Right Hand", so the ancestry answers it without either
        /// side needing to know about the other.
        /// </summary>
        private PressureTrackerMain TrackerFor(MonoBehaviour interactor)
        {
            if (interactor == null)
            {
                return rightPressureTracker != null ? rightPressureTracker : leftPressureTracker;
            }

            Transform t = interactor.transform;
            while (t != null)
            {
                string n = t.name;
                if (n.IndexOf("Left", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return leftPressureTracker;
                }

                if (n.IndexOf("Right", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return rightPressureTracker;
                }

                t = t.parent;
            }

            return rightPressureTracker != null ? rightPressureTracker : leftPressureTracker;
        }

        private void Apply(PressureTrackerMain tracker)
        {
            if (tracker == null)
            {
                return;
            }

            Release();              // never leave the other hand holding pressure
            activeTracker = tracker;
            activeFingers = tracker.GetComponent<FingerUseTracking>();
            appliedPressure = -1f;
            Push(followGrip ? PressureFromGrip() : strength);
        }

        private void Update()
        {
            if (activeTracker == null || !followGrip)
            {
                return;
            }

            Push(PressureFromGrip());
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
        private float PressureFromGrip()
        {
            // Re-checked every call rather than cached: FingerUseTracking's joint references are
            // scene-local, so across a scene load the component can survive while the thing it
            // reads does not.
            if (activeFingers == null)
            {
                return strength;
            }

            float sum = 0f;
            int n = 0;
            if (thumb) { sum += activeFingers.ThumbUse; n++; }
            if (index) { sum += activeFingers.IndexUse; n++; }
            if (middle) { sum += activeFingers.MiddleUse; n++; }
            if (ring) { sum += activeFingers.RingUse; n++; }
            if (pinky) { sum += activeFingers.LittleUse; n++; }

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
        /// gain.
        /// </summary>
        private void Push(float pressure)
        {
            if (activeTracker == null)
            {
                return;
            }

            float stepped = Mathf.Round(Mathf.Clamp(pressure, 0f, 60f) / 10f) * 10f;
            if (Mathf.Approximately(stepped, appliedPressure))
            {
                return;
            }

            appliedPressure = stepped;

            if (stepped <= 0f)
            {
                RemoveAll();
                return;
            }

            if (thumb) activeTracker.SingleThumbHaptic(stepped);
            if (index) activeTracker.SingleIndexHaptic(stepped);
            if (middle) activeTracker.SingleMiddleHaptic(stepped);
            if (ring) activeTracker.SingleRingHaptic(stepped);
            if (pinky) activeTracker.SinglePinkyHaptic(stepped);
            if (palm) activeTracker.SinglePalmHaptic(stepped);
        }

        private void RemoveAll()
        {
            if (activeTracker == null)
            {
                return;
            }

            if (thumb) activeTracker.RemoveThumbHaptics();
            if (index) activeTracker.RemoveIndexHaptics();
            if (middle) activeTracker.RemoveMiddleHaptics();
            if (ring) activeTracker.RemoveRingHaptics();
            if (pinky) activeTracker.RemovePinkyHaptics();
            if (palm) activeTracker.RemovePalmHaptics();
        }

        private void Release()
        {
            if (activeTracker == null)
            {
                return;
            }

            RemoveAll();
            activeTracker = null;
            activeFingers = null;
            appliedPressure = -1f;
        }
    }
}
