using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Gives a stock <see cref="XRGrabInteractable"/> a different grip per hand, and a switch between
/// a pinch hold and a fist hold -- without replacing the interactable.
///
/// Why it is built this way. A PICO app should be built the ordinary OpenXR way: XRGrabInteractable
/// and the hand interactors XRI ships, nothing bespoke in the grab path. So this does not subclass
/// the interactable or touch how grabbing works. It listens to <c>hoverEntered</c> -- the same
/// public UnityEvent anyone can hook -- and points the interactable's own Attach Transform at the
/// grip for whichever hand is reaching. XRI then grabs exactly as it always does.
///
/// Why hover and not select. XRGeneralGrabTransformer.OnGrab captures the attach transform once,
/// at the moment of the grab, so changing it afterwards has no effect on that grab. Hover always
/// precedes select in practice: with Select Action Trigger on State Change the pinch has to
/// transition while the object is already a valid target, which means hover frames have run first.
/// (The interaction manager does process select before hover within a frame, so an already-closed
/// hand arriving on an object is the one case this cannot catch -- and that case cannot select
/// either, for the same State Change reason.)
///
/// The hand frame this assumes, measured from the rig's own bind pose:
/// <b>+Z along the fingers, +Y out the back of the hand, +X toward the thumb</b> (mirrored to -X on
/// the right hand). X is the axis that flips between hands, which is the whole reason a fist hold
/// needs one grip each and a pinch hold does not.
/// </summary>

namespace HexR.OpenXR
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(XRGrabInteractable))]
    [AddComponentMenu("HexR/HexR Grip By Hand")]
    public class HexRGripByHand : MonoBehaviour
    {
        /// <summary>How the object is held.</summary>
        public enum GripMode
        {
            /// <summary>
            /// Held between thumb and index, extending out along the fingers -- a key, a pen, a wand.
            /// Uses the Attach Transform exactly as authored.
            /// </summary>
            Pinch,

            /// <summary>
            /// Gripped across the palm with the object's long axis running thumb-to-little-finger --
            /// a torch, a hammer, a handle. Rolls the authored grip 90 degrees so the object lies
            /// across the fist rather than pointing out of it.
            /// </summary>
            Fist,
        }

        [Tooltip("Pinch: the object extends out along the fingers, as authored. " +
                 "Fist: the same grip rolled 90 degrees so the object lies across the palm.")]
        public GripMode gripMode = GripMode.Pinch;

        [Tooltip("Derive the right hand's grip from the left. Leave on unless the object is genuinely " +
                 "asymmetric -- it keeps both hands consistent from one authored transform.")]
        public bool mirrorHands = true;

        [Tooltip("The authored grip. Leave empty to use the interactable's own Attach Transform.")]
        public Transform baseAttach;

        private XRGrabInteractable interactable;
        private Transform derivedLeft;
        private Transform derivedRight;
        private Transform originalAttach;

        private void Awake()
        {
            interactable = GetComponent<XRGrabInteractable>();
            originalAttach = interactable.attachTransform;
        }

        private void OnEnable()
        {
            interactable.hoverEntered.AddListener(OnHoverEntered);
        }

        private void OnDisable()
        {
            interactable.hoverEntered.RemoveListener(OnHoverEntered);

            // Put back whatever was authored, so turning this off leaves a plain interactable behind.
            if (interactable != null)
            {
                interactable.attachTransform = originalAttach;
            }
        }

        private void OnHoverEntered(HoverEnterEventArgs args)
        {
            InteractorHandedness hand = Handedness(args.interactorObject);
            if (hand == InteractorHandedness.None)
            {
                return;
            }

            Transform grip = GripFor(hand);
            if (grip != null)
            {
                interactable.attachTransform = grip;
            }
        }

        /// <summary>The grip transform for a hand, building it on first use.</summary>
        public Transform GripFor(InteractorHandedness hand)
        {
            if (!TryGetGripPose(hand, out Pose local))
            {
                return null;
            }

            bool left = hand == InteractorHandedness.Left;
            Transform t = left ? derivedLeft : derivedRight;

            if (t == null)
            {
                // Runtime-only, so switching mode or mirroring never leaves stale objects in the
                // scene and the scene file stays as small as the one authored grip.
                var go = new GameObject(left ? "HexR Grip (L)" : "HexR Grip (R)") { hideFlags = HideFlags.DontSave };
                t = go.transform;
                t.SetParent(transform, false);

                if (left)
                {
                    derivedLeft = t;
                }
                else
                {
                    derivedRight = t;
                }
            }

            t.localPosition = local.position;
            t.localRotation = local.rotation;
            t.localScale = Vector3.one;
            return t;
        }

        /// <summary>
        /// The grip pose for a hand, in this object's local space. The Scene-view preview reads this
        /// too, so what you author is what you get when you grab.
        /// </summary>
        public bool TryGetGripPose(InteractorHandedness hand, out Pose local)
        {
            local = default;

            Transform source = baseAttach != null ? baseAttach
                : (interactable != null ? interactable.attachTransform : GetComponent<XRGrabInteractable>()?.attachTransform);

            // originalAttach is the authored one; once hovering has repointed attachTransform at a
            // derived grip, reading it back would compound the roll every hover.
            if (originalAttach != null && baseAttach == null)
            {
                source = originalAttach;
            }

            if (source == null)
            {
                return false;
            }

            Quaternion roll = ModeRoll(gripMode);
            if (hand == InteractorHandedness.Right)
            {
                roll *= MirrorRoll(gripMode);
            }

            // Position is shared between hands on purpose. Mirroring it would need the object's plane
            // of symmetry, which nothing here knows; for a grip on the object's own axis -- a shaft, a
            // handle, a key -- the same point is correct for both hands anyway.
            local = new Pose(
                transform.InverseTransformPoint(source.position),
                (Quaternion.Inverse(transform.rotation) * source.rotation) * roll);
            return true;
        }

        /// <summary>The roll applied on top of the authored grip for the current mode.</summary>
        public static Quaternion ModeRoll(GripMode mode)
        {
            // -90 about the attach's own Y takes "points out along the fingers" to "lies across the
            // palm, toward the thumb".
            return mode == GripMode.Fist ? Quaternion.AngleAxis(-90f, Vector3.up) : Quaternion.identity;
        }

        /// <summary>
        /// The roll that turns a left-hand grip into the matching right-hand one, for a given mode.
        ///
        /// A true mirror is a reflection, and no rotation can express one -- so this is not a general
        /// mirror, it is the specific rotation that is correct per mode:
        ///
        /// <b>Fist</b> - the object lies along the thumb-ward axis, which is the axis that flips
        /// between hands. A 180 about Y flips it correctly. That also flips Z, but for a fist grip Z is
        /// a roll about the object's own long axis, invisible on a shaft or a handle.
        ///
        /// <b>Pinch</b> - the object extends along the fingers (+Z) and the grip does not use X at all,
        /// so it is *already* correct in both hands. Mirroring must be a no-op here; the same 180 would
        /// send the object pointing back at the wrist.
        /// </summary>
        public static Quaternion MirrorRoll(GripMode mode)
        {
            return mode == GripMode.Fist ? Quaternion.AngleAxis(180f, Vector3.up) : Quaternion.identity;
        }

        /// <summary>
        /// Which hand an interactor belongs to. Prefers XRI's own handedness field, falling back to
        /// the object's name because the hand interactors in the XRI 2.5.4 sample rig predate that
        /// field -- the same side-matching fallback HexRInteractableHaptics uses.
        /// </summary>
        public static InteractorHandedness Handedness(IXRInteractor interactor)
        {
            if (interactor is XRBaseInteractor baseInteractor
                && baseInteractor.handedness != InteractorHandedness.None)
            {
                return baseInteractor.handedness;
            }

            Transform t = interactor?.transform;
            while (t != null)
            {
                if (t.name.IndexOf("Left", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return InteractorHandedness.Left;
                }

                if (t.name.IndexOf("Right", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return InteractorHandedness.Right;
                }

                t = t.parent;
            }

            return InteractorHandedness.None;
        }
    }
}
