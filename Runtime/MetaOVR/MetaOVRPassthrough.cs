using UnityEngine;

namespace HexR.MetaOVR
{
    /// <summary>
    /// Passthrough on Meta, for the shared HexR menu.
    ///
    /// The mirror of the PICO project's own passthrough component: same interface, same button,
    /// different runtime underneath. Neither can live in <c>HexR.Runtime</c> -- PICO's needs the
    /// vendored PICO OpenXR plugin and this one needs <c>Oculus.VR</c> -- so the menu talks to
    /// <see cref="IHexRPassthrough"/> and each side answers for itself.
    ///
    /// Showing the camera feed takes two things together, and the second is the one people miss:
    /// the passthrough layer has to be visible, *and* the camera has to stop painting a background
    /// over it. Both are restored on the way back so a scene that started in VR looks unchanged.
    /// </summary>
    [AddComponentMenu("HexR/Meta OVR Passthrough")]
    public class MetaOVRPassthrough : MonoBehaviour, IHexRPassthrough
    {
        [Tooltip("Leave empty -- found automatically. Set it only when a scene has more than one.")]
        public OVRPassthroughLayer layer;

        [Tooltip("Start the scene in passthrough. Untick to start in VR.")]
        public bool startInPassthrough = true;

        private Camera cam;
        private CameraClearFlags savedFlags;
        private Color savedBackground;
        private bool saved;

        // The mirror of the PICO component's Start, so both demos come up in passthrough with no
        // button to press. Start rather than Awake: Resolve needs the OVRPassthroughLayer and
        // ApplyCamera needs Camera.main, and neither is reliably there the frame a rig awakes.
        private void Start()
        {
            if (startInPassthrough)
            {
                SetPassthrough(true);
            }
        }

        public bool IsAvailable
        {
            // Asking must not build one. Only an actual request for passthrough does that.
            get { return Resolve(false) != null; }
        }

        public bool IsOn
        {
            get
            {
                OVRPassthroughLayer l = Resolve(false);
                return l != null && !l.hidden;
            }
        }

        public void Toggle()
        {
            SetPassthrough(!IsOn);
        }

        /// <summary>
        /// The rig survives scene loads; the camera whose background it needs cleared does not.
        /// Dropping the cached camera makes the next call find the new one.
        /// </summary>
        public void RefreshCamera()
        {
            cam = null;
            saved = false;

            // Re-apply to the new camera, so crossing a scene boundary in passthrough does not
            // come back as a black void.
            if (IsOn)
            {
                ApplyCamera(true);
            }
        }

        public void SetPassthrough(bool on)
        {
            OVRPassthroughLayer l = Resolve(on);
            if (l == null)
            {
                return;
            }

            l.hidden = !on;
            ApplyCamera(on);
        }

        /// <summary>
        /// The passthrough layer, created if the scene has none and <paramref name="create"/>.
        ///
        /// Creating it is the point. Neither tutorial scene ships an OVRPassthroughLayer, and
        /// without one this component is inert -- which is what "Passthrough - headset only"
        /// used to be reporting: not an unsupported headset, just a scene with no layer in it.
        /// Adding it here rather than to the scenes means every Meta scene gets passthrough
        /// without each one being edited, and a scene that already has a layer keeps its own.
        ///
        /// A fresh layer already defaults to Underlay + Reconstructed + visible, which is exactly
        /// full-screen passthrough, so nothing here restates those.
        /// </summary>
        private OVRPassthroughLayer Resolve(bool create)
        {
            if (layer != null)
            {
                return layer;
            }

            layer = HexRCompat.FindAny<OVRPassthroughLayer>(true);
            if (layer != null || !create)
            {
                return layer;
            }

            // No OVRManager means this is not a Meta rig, and a layer would have nothing to talk
            // to -- leave the scene alone rather than adding a component that cannot work.
            OVRManager manager = HexRCompat.FindAny<OVRManager>(true);
            if (manager == null)
            {
                return null;
            }

            // The layer renders nothing unless Insight is on; both tutorial scenes already set
            // it, but a scene built from scratch will not.
            manager.isInsightPassthroughEnabled = true;
            layer = manager.gameObject.AddComponent<OVRPassthroughLayer>();
            return layer;
        }

        private void ApplyCamera(bool on)
        {
            if (cam == null)
            {
                cam = Camera.main;
            }

            if (cam == null)
            {
                return;
            }

            if (on)
            {
                if (!saved)
                {
                    savedFlags = cam.clearFlags;
                    savedBackground = cam.backgroundColor;
                    saved = true;
                }

                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            }
            else if (saved)
            {
                cam.clearFlags = savedFlags;
                cam.backgroundColor = savedBackground;
                saved = false;
            }
        }
    }
}
