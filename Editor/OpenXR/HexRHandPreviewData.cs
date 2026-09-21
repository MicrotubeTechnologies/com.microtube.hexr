using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Reads the hand skeleton out of the XRI sample's hand visual prefabs once, and caches it as a
/// flat wrist-relative table for <see cref="HexRGripAttachPreview"/> to draw.
///
/// Why wrist-relative. The preview places a hand by solving for the wrist pose that puts the
/// hand's attach frame exactly on an object's Grip Attach transform. Once that wrist pose is
/// known, everything else is a single Handles.matrix and a table of constants -- no per-joint
/// world-space maths, and no dependency on where the prefab happens to sit.
///
/// Read from the prefab rather than hardcoded so the proportions are the real rig's, and so a
/// different hand model shows up as a different preview instead of a silently stale one.
/// </summary>

namespace HexR.OpenXR
{
    internal static class HexRHandPreviewData
    {
        internal enum Hand
        {
            Left,
            Right,
        }

        /// <summary>One hand's joints, all expressed relative to the wrist.</summary>
        internal sealed class HandSkeleton
        {
            public string[] names;
            public int[] parent;            // index into the arrays; -1 for the wrist itself
            public Vector3[] position;      // wrist-local
            public Quaternion[] rotation;   // wrist-local

            /// <summary>
            /// Midpoint of the thumb and index fingertips -- the point the interactor's attach
            /// transform sits on. Derived rather than hardcoded so it stays honest if the drawn
            /// pose ever changes.
            /// </summary>
            public Vector3 pinchLocal;

            public int thumbTip = -1;
            public int indexTip = -1;
            public int Count => names.Length;
        }

        // Assets/Samples/XR Interaction Toolkit/2.5.4/Hands Interaction Demo/Prefabs/
        private const string LeftGuid = "ffd656bf2a3ba3d41b1e4a94b81b7c85";
        private const string RightGuid = "89e80c47615e4f043926d66492d3ca5f";

        private static readonly Dictionary<Hand, HandSkeleton> Cache = new Dictionary<Hand, HandSkeleton>();
        private static bool warnedMissing;

        /// <summary>The skeleton for one hand, or null if the sample prefabs can't be found.</summary>
        internal static HandSkeleton Get(Hand hand)
        {
            if (Cache.TryGetValue(hand, out HandSkeleton cached))
            {
                return cached;
            }

            HandSkeleton built = Build(hand);
            Cache[hand] = built;
            return built;
        }

        /// <summary>Drop the cache so the next Get re-reads the prefabs.</summary>
        internal static void Invalidate()
        {
            Cache.Clear();
            warnedMissing = false;
        }

        private static HandSkeleton Build(Hand hand)
        {
            GameObject prefab = LoadPrefab(hand);
            if (prefab == null)
            {
                // One warning per session, never per-frame -- this runs from duringSceneGui.
                if (!warnedMissing)
                {
                    warnedMissing = true;
                    Debug.LogWarning("[HexR] Grip Attach Preview can't find the XRI hand visual prefabs " +
                                     "(Hands Interaction Demo sample). The preview will not draw.");
                }

                return null;
            }

            Transform wrist = FindWrist(prefab.transform);
            if (wrist == null)
            {
                if (!warnedMissing)
                {
                    warnedMissing = true;
                    Debug.LogWarning("[HexR] Grip Attach Preview found '" + prefab.name +
                                     "' but no *_Wrist bone inside it. The preview will not draw.");
                }

                return null;
            }

            // Everything is taken relative to the wrist, so wherever the prefab root sits is
            // irrelevant -- and going through matrices rather than localPosition/localRotation keeps
            // any scale on the intermediate bones correct.
            Matrix4x4 toWrist = wrist.worldToLocalMatrix;

            var transforms = new List<Transform>();
            Collect(wrist, transforms);

            var skel = new HandSkeleton
            {
                names = new string[transforms.Count],
                parent = new int[transforms.Count],
                position = new Vector3[transforms.Count],
                rotation = new Quaternion[transforms.Count],
            };

            for (int i = 0; i < transforms.Count; i++)
            {
                Transform t = transforms[i];
                Matrix4x4 m = toWrist * t.localToWorldMatrix;

                skel.names[i] = t.name;
                skel.position[i] = m.GetColumn(3);
                skel.rotation[i] = m.rotation;
                skel.parent[i] = i == 0 ? -1 : transforms.IndexOf(t.parent);

                if (t.name.EndsWith("ThumbTip")) skel.thumbTip = i;
                else if (t.name.EndsWith("IndexTip")) skel.indexTip = i;
            }

            skel.pinchLocal = skel.thumbTip >= 0 && skel.indexTip >= 0
                ? Vector3.Lerp(skel.position[skel.thumbTip], skel.position[skel.indexTip], 0.5f)
                : Vector3.zero;

            return skel;
        }

        private static void Collect(Transform root, List<Transform> into)
        {
            into.Add(root);
            for (int i = 0; i < root.childCount; i++)
            {
                Transform c = root.GetChild(i);

                // The hand visual also carries the skinned mesh and the interactor affordance
                // objects. Only the bone chain is wanted.
                if (c.GetComponent<Renderer>() != null)
                {
                    continue;
                }

                Collect(c, into);
            }
        }

        private static Transform FindWrist(Transform root)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.EndsWith("_Wrist"))
                {
                    return t;
                }
            }

            return null;
        }

        private static GameObject LoadPrefab(Hand hand)
        {
            string guid = hand == Hand.Left ? LeftGuid : RightGuid;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = string.IsNullOrEmpty(path)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(path);

            if (prefab != null)
            {
                return prefab;
            }

            // Re-importing the sample at another version gives it fresh GUIDs, so fall back to name.
            string wanted = hand == Hand.Left ? "Left Hand Interaction Visual" : "Right Hand Interaction Visual";
            foreach (string found in AssetDatabase.FindAssets("\"" + wanted + "\" t:GameObject"))
            {
                string p = AssetDatabase.GUIDToAssetPath(found);
                if (p.EndsWith(wanted + ".prefab"))
                {
                    return AssetDatabase.LoadAssetAtPath<GameObject>(p);
                }
            }

            return null;
        }
    }
}
