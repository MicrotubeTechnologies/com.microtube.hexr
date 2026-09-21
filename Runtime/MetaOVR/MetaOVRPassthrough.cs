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

        private Camera cam;
        private CameraClearFlags savedFlags;
        private Color savedBackground;
        private bool saved;

        public bool IsAvailable
        {
            get { return Resolve() != null; }
        }

        public bool IsOn
        {
            get
            {
                OVRPassthroughLayer l = Resolve();
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
            OVRPassthroughLayer l = Resolve();
            if (l == null)
            {
                return;
            }

            l.hidden = !on;
            ApplyCamera(on);
        }

        private OVRPassthroughLayer Resolve()
        {
            if (layer == null)
            {
                layer = HexRCompat.FindAny<OVRPassthroughLayer>(true);
            }

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
