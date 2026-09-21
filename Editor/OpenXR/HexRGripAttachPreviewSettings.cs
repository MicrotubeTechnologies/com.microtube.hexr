using UnityEditor;
using UnityEngine;

/// <summary>
/// Preferences for <see cref="HexRGripAttachPreview"/>, kept entirely in EditorPrefs.
///
/// Nothing here is serialized into a scene or a prefab -- the preview must not be able to leave
/// a trace in the project, and per-user tool state has no business in version control anyway.
/// </summary>

namespace HexR.OpenXR
{
    internal static class HexRGripAttachPreviewSettings
    {
        private const string Prefix = "HexR.GripAttachPreview.";

        internal enum HandsShown
        {
            Left,
            Right,
            Both,
        }

        internal static bool Enabled
        {
            get => EditorPrefs.GetBool(Prefix + "Enabled", false);
            set => EditorPrefs.SetBool(Prefix + "Enabled", value);
        }

        internal static HandsShown Hands
        {
            get => (HandsShown)EditorPrefs.GetInt(Prefix + "Hands", (int)HandsShown.Both);
            set => EditorPrefs.SetInt(Prefix + "Hands", (int)value);
        }

        internal static bool ShowLabels
        {
            get => EditorPrefs.GetBool(Prefix + "ShowLabels", true);
            set => EditorPrefs.SetBool(Prefix + "ShowLabels", value);
        }

        internal static float Opacity
        {
            get => EditorPrefs.GetFloat(Prefix + "Opacity", 0.9f);
            set => EditorPrefs.SetFloat(Prefix + "Opacity", Mathf.Clamp01(value));
        }

        /// <summary>
        /// Whether a measured attach frame has replaced the one derived from the skeleton.
        ///
        /// The derived default assumes the interactor's attach transform sits at the pinch point with
        /// the wrist's own rotation. That held when traced through the Direct Interactor's
        /// ActionBasedController (position from "XRI * /Pinch Position", rotation from "XRI * /Rotation"),
        /// but the rig has other interactors that do not follow that rule -- PinchPointFollow, for one,
        /// takes its rotation from the ray direction. Capturing the real frame in Play mode replaces
        /// the assumption with a measurement.
        /// </summary>
        internal static bool HasCapturedFrame
        {
            get => EditorPrefs.GetBool(Prefix + "HasCapture", false);
            set => EditorPrefs.SetBool(Prefix + "HasCapture", value);
        }

        /// <summary>Captured attach position, in wrist-local space, for the LEFT hand. Right is mirrored in X.</summary>
        internal static Vector3 CapturedPosition
        {
            get => GetVector(Prefix + "CapPos", Vector3.zero);
            set => SetVector(Prefix + "CapPos", value);
        }

        /// <summary>Captured attach rotation, relative to the wrist, as euler degrees.</summary>
        internal static Vector3 CapturedEuler
        {
            get => GetVector(Prefix + "CapEuler", Vector3.zero);
            set => SetVector(Prefix + "CapEuler", value);
        }

        internal static void ClearCapture()
        {
            HasCapturedFrame = false;
            CapturedPosition = Vector3.zero;
            CapturedEuler = Vector3.zero;
        }

        private static Vector3 GetVector(string key, Vector3 fallback)
        {
            return new Vector3(
                EditorPrefs.GetFloat(key + ".x", fallback.x),
                EditorPrefs.GetFloat(key + ".y", fallback.y),
                EditorPrefs.GetFloat(key + ".z", fallback.z));
        }

        private static void SetVector(string key, Vector3 v)
        {
            EditorPrefs.SetFloat(key + ".x", v.x);
            EditorPrefs.SetFloat(key + ".y", v.y);
            EditorPrefs.SetFloat(key + ".z", v.z);
        }
    }
}
