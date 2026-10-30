using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HexR
{
    /// <summary>
    /// Puts the wearer back on the scene's starting spot, facing its front: for someone who has
    /// wandered off, or was handed the headset facing the wrong way. HexR Remote's "recenter"
    /// request calls it, and an app can too.
    ///
    /// Works by moving the camera rig, not the tracking origin, so it is the same on Meta, PICO and
    /// any OpenXR runtime, and needs no system permission. The starting spot is where the rig
    /// stood when the scene loaded; the rig is the camera's nearest OVRCameraRig, XR Origin or XR
    /// Rig, else the camera's topmost parent.
    /// </summary>
    public static class HexRRecenter
    {
        /// <summary>Raised after a recenter, so an app can bring what floats in front of the wearer round too.</summary>
        public static event Action Recentered;

        private static readonly string[] RigTypes = { "OVRCameraRig", "XROrigin", "XRRig" };

        private static Transform startRig;
        private static Vector3 startPosition;
        private static float startYaw;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Single)
            {
                startRig = null;
                RememberStart();
            }
        }

        // The rig where it stands now, as the starting spot. Lazily too, for a camera that was not
        // there yet when the scene loaded.
        private static Transform RememberStart()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                return null;
            }

            Transform rig = RigOf(camera.transform);
            if (rig != startRig || startRig == null)
            {
                startRig = rig;
                startPosition = rig.position;
                startYaw = rig.eulerAngles.y;
            }

            return rig;
        }

        /// <summary>The camera's rig: its nearest known XR rig, else its topmost parent.</summary>
        public static Transform RigOf(Transform camera)
        {
            for (Transform t = camera.parent; t != null; t = t.parent)
            {
                foreach (Component c in t.GetComponents<Component>())
                {
                    if (c != null && Array.IndexOf(RigTypes, c.GetType().Name) >= 0)
                    {
                        return t;
                    }
                }
            }

            return camera.root;
        }

        /// <summary>
        /// Turns and moves the rig so the wearer's head is over the starting spot, facing the way
        /// the rig faced there. Returns null, or why it could not.
        /// </summary>
        public static string Recenter()
        {
            Transform rig = RememberStart();
            if (rig == null)
            {
                return "There is no camera in this scene to recenter.";
            }

            Camera camera = Camera.main;
            Transform head = camera.transform;
            if (head == rig)
            {
                return "The camera has no rig to move.";
            }

            // A CharacterController puts the rig back where it thinks it is unless it is off while
            // the rig moves.
            CharacterController body = rig.GetComponent<CharacterController>();
            bool bodyWasOn = body != null && body.enabled;
            if (bodyWasOn)
            {
                body.enabled = false;
            }

            Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            float headYaw = forward.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(forward).eulerAngles.y : rig.eulerAngles.y;
            rig.RotateAround(head.position, Vector3.up, Mathf.DeltaAngle(headYaw, startYaw));

            Vector3 offset = startPosition - head.position;
            rig.position += new Vector3(offset.x, 0f, offset.z);

            if (bodyWasOn)
            {
                body.enabled = true;
            }

            Debug.Log("[HexR] Recentered the wearer on the scene's starting spot.");
            Recentered?.Invoke();
            return null;
        }
    }
}
