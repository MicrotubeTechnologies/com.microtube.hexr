using UnityEngine;
using UnityEngine.Events;

namespace HexR
{
    /// <summary>
    /// A button you press with a finger, not a pointer.
    ///
    /// Why this exists. Every other way of putting a control in front of a user goes through some
    /// SDK's UI stack -- a Canvas, a raycaster, an input module, an interactor that emits a ray or
    /// a poke -- and each of those is different on Meta and on OpenXR, has to be wired per rig, and
    /// fails in ways that look like "the panel does nothing". This needs none of it. It is a
    /// trigger collider that notices one of HexR's own fingertip colliders arriving, so it behaves
    /// identically on every backend, including ones that do not exist yet.
    ///
    /// The only requirement is a HexR rig: <see cref="HapticFingerTrigger"/> on the fingertips is
    /// what identifies the finger, and Auto Setup places those. No rig, no press.
    ///
    /// It also feels like a button, which a floating canvas never does --
    /// <see cref="HapticFingerTrigger.TriggerFixPressure"/> squeezes the finger that pressed it,
    /// and that call bypasses the hand-near gate, so the feedback lands even in a scene where
    /// nothing else has told HexR a hand is nearby.
    /// </summary>
    [AddComponentMenu("HexR/Physical Button")]
    [DisallowMultipleComponent]
    public class HexRPhysicalButton : MonoBehaviour
    {
        [Header("What it does")]
        // Constructed here, not just declared. Unity builds serialised UnityEvent fields when it
        // deserialises a component from a scene or prefab, but a component added at runtime with
        // AddComponent gets none of that -- the field is simply null, and the first AddListener
        // throws. The menu builds all of its buttons that way.
        public UnityEvent onPressed = new UnityEvent();
        public UnityEvent onReleased = new UnityEvent();

        [Header("Which fingers can press it")]
        [Tooltip("An index-only button is the predictable one -- a palm or a curled ring finger " +
                 "brushing past should not fire it.")]
        public bool thumb = false;
        public bool index = true;
        public bool middle = false;
        public bool ring = false;
        public bool pinky = false;
        public bool palm = false;

        [Header("Feel")]
        [Tooltip("The part that visibly moves. Leave empty to move this object itself.")]
        public Transform cap;

        [Tooltip("How far the cap sinks, in metres, along Press Direction.")]
        public float travel = 0.006f;

        [Tooltip("Local-space direction the cap travels when pressed.")]
        public Vector3 pressDirection = new Vector3(0f, 0f, -1f);

        [Tooltip("Pressure applied to the pressing finger while held. 0 is off, 60 is firm.")]
        [Range(0f, 60f)]
        public float pressPressure = 40f;

        [Tooltip("Ignore a new press for this long after one ends, so a finger resting on the " +
                 "boundary cannot chatter the button.")]
        public float rearmSeconds = 0.2f;

        // Only the finger that pressed it can release it. Without this, a second finger entering
        // fires a press the first finger's exit then cancels, which reads as the button
        // double-firing when two fingers arrive together -- which they usually do.
        private HapticFingerTrigger presser;
        private Vector3 capRestPosition;
        private bool capCaptured;
        private float rearmAt;

        private void Awake()
        {
            CaptureRest();
        }

        private void CaptureRest()
        {
            if (cap == null)
            {
                cap = transform;
            }

            if (!capCaptured)
            {
                capRestPosition = cap.localPosition;
                capCaptured = true;
            }
        }

        private void OnEnable()
        {
            // A press that was in progress cannot survive a disable -- the finger's exit would
            // never arrive, leaving the cap sunk and the finger squeezed.
            Release();
        }

        private void OnDisable()
        {
            Release();
        }

        private void OnTriggerEnter(Collider other)
        {
            if (presser != null || Time.time < rearmAt)
            {
                return;
            }

            HapticFingerTrigger trigger = other.GetComponent<HapticFingerTrigger>();
            if (trigger == null || !Accepts(trigger.fingertype))
            {
                return;
            }

            CaptureRest();
            presser = trigger;
            cap.localPosition = capRestPosition + pressDirection.normalized * travel;

            // The action first, the feel second. These used to run the other way round, and a
            // throw inside the haptics -- which is what an unpaired glove produced -- unwound out
            // of OnTriggerEnter before ever reaching this line, so the button lit up, sank, and
            // did nothing. Feedback is the part that may fail; what the button is for is not.
            onPressed?.Invoke();

            if (pressPressure > 0f)
            {
                trigger.TriggerFixPressure(pressPressure);
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (presser == null || other == null || other.GetComponent<HapticFingerTrigger>() != presser)
            {
                return;
            }

            onReleased?.Invoke();
            Release();
        }

        private void Release()
        {
            if (presser != null)
            {
                presser.RemoveHaptics();
                presser = null;
                rearmAt = Time.time + rearmSeconds;
            }

            if (capCaptured && cap != null)
            {
                cap.localPosition = capRestPosition;
            }
        }

        private bool Accepts(HapticFingerTrigger.FingerType finger)
        {
            switch (finger)
            {
                case HapticFingerTrigger.FingerType.Thumb:  return thumb;
                case HapticFingerTrigger.FingerType.Index:  return index;
                case HapticFingerTrigger.FingerType.Middle: return middle;
                case HapticFingerTrigger.FingerType.Ring:   return ring;
                case HapticFingerTrigger.FingerType.Little: return pinky;
                case HapticFingerTrigger.FingerType.Palm:   return palm;
                default: return false;
            }
        }
    }
}
