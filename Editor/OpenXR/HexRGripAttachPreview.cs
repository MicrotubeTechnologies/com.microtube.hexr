using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Draws a hand in the Scene view, posed as if it were holding the selected grabbable, so a
/// Grip Attach transform can be aligned by eye instead of by arithmetic.
///
/// Why this exists. With Use Dynamic Attach off, XRI drives the interactable's Attach Transform
/// until its WORLD pose equals the interactor's attach pose. So the Grip Attach child's local
/// position and rotation completely determine how the object sits in the hand -- but the attach
/// frame is invisible and the hand isn't in the scene, so there is nothing to align against. You
/// end up changing numbers, building to the headset, and guessing again.
///
/// This inverts that relationship. Given the Grip Attach pose, solve for where the wrist would
/// have to be, and draw the hand there. Drag the ordinary move/rotate gizmo and the hand follows.
///
/// It draws with Handles in immediate mode and creates no GameObjects at all, which is the point:
/// it cannot leak an object into a scene or become an override on a prefab, and it survives a
/// domain reload with nothing to clean up. A wireframe also reads better here than a solid hand
/// would -- a solid mesh wrapped round a torch shaft hides the very thing you are judging.
///
/// Both hands are drawn by default because the hand frame's X axis points at the thumb, which
/// mirrors: a grip that leans on X looks right in one hand and wrong in the other.
/// </summary>

namespace HexR.OpenXR
{
    [InitializeOnLoad]
    internal static class HexRGripAttachPreview
    {
        private const string MenuPath = "Tools/HexR/Grip Attach Preview";
        private const int MaxTargets = 8;

        private static readonly List<XRGrabInteractable> Targets = new List<XRGrabInteractable>();

        private static readonly Dictionary<Transform, Matrix4x4> LastSeen = new Dictionary<Transform, Matrix4x4>();

        /// <summary>
        /// The world pose this hand would actually grip at.
        ///
        /// For a <see cref="HexRHandedGrabInteractable"/> this asks the component itself, so the
        /// preview and the runtime cannot disagree about what the mode and mirror settings mean.
        /// </summary>
        private static bool TryGripPose(XRGrabInteractable grab, HexRHandPreviewData.Hand hand, out Pose world)
        {
            var byHand = grab.GetComponent<HexRGripByHand>();
            if (byHand != null)
            {
                InteractorHandedness side = hand == HexRHandPreviewData.Hand.Left
                    ? InteractorHandedness.Left
                    : InteractorHandedness.Right;

                if (byHand.TryGetGripPose(side, out Pose local))
                {
                    world = new Pose(
                        grab.transform.TransformPoint(local.position),
                        grab.transform.rotation * local.rotation);
                    return true;
                }
            }

            Transform attach = grab.attachTransform;
            if (attach == null)
            {
                world = default;
                return false;
            }

            world = new Pose(attach.position, attach.rotation);
            return true;
        }

        static HexRGripAttachPreview()
        {
            Restore();
        }

        /// <summary>
        /// Re-attach after a domain reload.
        ///
        /// Subscribing directly rather than through EditorApplication.delayCall: a queued delayCall
        /// can be lost across a reload, which leaves the tool switched on in preferences but silently
        /// drawing nothing -- it looks broken rather than off, and that cost real debugging time.
        /// Subscribe only touches delegates, so there is nothing here that needs deferring.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void Restore()
        {
            if (HexRGripAttachPreviewSettings.Enabled)
            {
                Subscribe();
            }
        }

        [MenuItem(MenuPath, priority = 100)]
        private static void Toggle()
        {
            bool on = !HexRGripAttachPreviewSettings.Enabled;
            HexRGripAttachPreviewSettings.Enabled = on;

            if (on)
            {
                Subscribe();
            }
            else
            {
                Unsubscribe();
            }

            SceneView.RepaintAll();
        }

        [MenuItem(MenuPath, validate = true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, HexRGripAttachPreviewSettings.Enabled);
            return true;
        }

        [MenuItem("Tools/HexR/Reload Hand Preview Data", priority = 101)]
        private static void ReloadData()
        {
            HexRHandPreviewData.Invalidate();
            SceneView.RepaintAll();
        }

        internal static void Subscribe()
        {
            // Always detach first: a domain reload can leave a stale delegate behind, and double
            // subscription would draw (and repaint) twice.
            Unsubscribe();
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += Tick;
            Selection.selectionChanged += OnSelectionChanged;
        }

        internal static void Unsubscribe()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= Tick;
            Selection.selectionChanged -= OnSelectionChanged;
            LastSeen.Clear();
        }

        private static void OnSelectionChanged()
        {
            LastSeen.Clear();
            SceneView.RepaintAll();
        }

        /// <summary>
        /// Repaint when a watched attach transform moves. duringSceneGui alone misses edits typed
        /// into the Inspector, arrow-key nudges and undo, all of which should update the preview.
        /// </summary>
        private static void Tick()
        {
            if (!HexRGripAttachPreviewSettings.Enabled || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            ResolveTargets();

            bool changed = false;
            foreach (XRGrabInteractable grab in Targets)
            {
                // Watch the resolved grip pose rather than a transform, so a change to the grip mode
                // or the mirror toggle repaints just like dragging the gizmo does.
                if (!TryGripPose(grab, HexRHandPreviewData.Hand.Left, out Pose left))
                {
                    continue;
                }

                TryGripPose(grab, HexRHandPreviewData.Hand.Right, out Pose right);
                Matrix4x4 now = Matrix4x4.TRS(left.position, left.rotation, Vector3.one)
                                * Matrix4x4.TRS(right.position, right.rotation, Vector3.one);

                if (!LastSeen.TryGetValue(grab.transform, out Matrix4x4 before) || before != now)
                {
                    LastSeen[grab.transform] = now;
                    changed = true;
                }
            }

            if (changed)
            {
                SceneView.RepaintAll();
            }
        }

        /// <summary>
        /// Collect the grabbables to preview from the current selection. Resolves upward, because
        /// Grip Attach is a child of the interactable and is usually what's selected while dragging.
        /// </summary>
        private static void ResolveTargets()
        {
            Targets.Clear();

            foreach (Transform t in Selection.transforms)
            {
                if (Targets.Count >= MaxTargets)
                {
                    break;
                }

                var grab = t.GetComponentInParent<XRGrabInteractable>(true);
                if (grab == null || grab.useDynamicAttach)
                {
                    // Dynamic attach keeps whatever orientation the object was resting in, so there
                    // is no authored grip to preview and drawing one would be a lie.
                    continue;
                }

                if (!Targets.Contains(grab))
                {
                    Targets.Add(grab);
                }
            }
        }

        private static void OnSceneGUI(SceneView view)
        {
            if (!HexRGripAttachPreviewSettings.Enabled
                || EditorApplication.isPlayingOrWillChangePlaymode
                || Event.current.type != EventType.Repaint)
            {
                return;
            }

            ResolveTargets();

            foreach (XRGrabInteractable grab in Targets)
            {
                HexRGripAttachPreviewSettings.HandsShown which = HexRGripAttachPreviewSettings.Hands;

                if (which != HexRGripAttachPreviewSettings.HandsShown.Right
                    && TryGripPose(grab, HexRHandPreviewData.Hand.Left, out Pose left))
                {
                    DrawFor(HexRHandPreviewData.Hand.Left, left, new Color(0.35f, 0.75f, 1f));
                    DrawGripAnnotation(left, grab, "L");
                }

                if (which != HexRGripAttachPreviewSettings.HandsShown.Left
                    && TryGripPose(grab, HexRHandPreviewData.Hand.Right, out Pose right))
                {
                    DrawFor(HexRHandPreviewData.Hand.Right, right, new Color(1f, 0.6f, 0.3f));
                    DrawGripAnnotation(right, grab, "R");
                }
            }
        }

        /// <summary>
        /// The frame the interactor's attach transform occupies, expressed in wrist-local space.
        /// Derived from the skeleton by default; replaced by a measurement once captured.
        /// </summary>
        internal static Pose AttachFrameInWristSpace(HexRHandPreviewData.HandSkeleton skel, HexRHandPreviewData.Hand hand)
        {
            if (HexRGripAttachPreviewSettings.HasCapturedFrame)
            {
                Vector3 p = HexRGripAttachPreviewSettings.CapturedPosition;
                if (hand == HexRHandPreviewData.Hand.Right)
                {
                    p.x = -p.x;
                }

                return new Pose(p, Quaternion.Euler(HexRGripAttachPreviewSettings.CapturedEuler));
            }

            return new Pose(skel.pinchLocal, Quaternion.identity);
        }

        /// <summary>Diagnostics: last reason DrawFor bailed, and how many bones it last drew.</summary>
        internal static string LastDrawStatus = "never called";

        private static void DrawFor(HexRHandPreviewData.Hand hand, Pose attach, Color colour)
        {
            HexRHandPreviewData.HandSkeleton skel = HexRHandPreviewData.Get(hand);
            if (skel == null)
            {
                LastDrawStatus = hand + ": skeleton null";
                return;
            }

            LastDrawStatus = hand + ": drew " + skel.Count + " joints at " + attach.position.ToString("F3");

            Pose frame = AttachFrameInWristSpace(skel, hand);

            // Solve for the wrist pose that puts the hand's attach frame exactly on this transform.
            // Scale is deliberately ignored: the attach transform's world position already carries
            // the object's scale, and the hand is always life-size -- which is what XRI does too.
            Quaternion wristRot = attach.rotation * Quaternion.Inverse(frame.rotation);
            Vector3 wristPos = attach.position - (wristRot * frame.position);

            Matrix4x4 restore = Handles.matrix;
            Color restoreColour = Handles.color;
            CompareFunction restoreZ = Handles.zTest;

            Handles.matrix = Matrix4x4.TRS(wristPos, wristRot, Vector3.one);

            float alpha = HexRGripAttachPreviewSettings.Opacity;

            // Two passes so the hand is still legible where the object occludes it, without the
            // occluded part being mistaken for the real silhouette.
            Handles.zTest = CompareFunction.LessEqual;
            DrawSkeleton(skel, new Color(colour.r, colour.g, colour.b, alpha), true);

            Handles.zTest = CompareFunction.Always;
            DrawSkeleton(skel, new Color(colour.r, colour.g, colour.b, alpha * 0.18f), false);

            Handles.zTest = restoreZ;
            Handles.color = restoreColour;
            Handles.matrix = restore;
        }

        private static void DrawSkeleton(HexRHandPreviewData.HandSkeleton skel, Color colour, bool withJoints)
        {
            Handles.color = colour;

            for (int i = 1; i < skel.Count; i++)
            {
                int p = skel.parent[i];
                if (p < 0)
                {
                    continue;
                }

                Handles.DrawAAPolyLine(3f, skel.position[p], skel.position[i]);
            }

            if (!withJoints)
            {
                return;
            }

            for (int i = 0; i < skel.Count; i++)
            {
                Handles.SphereHandleCap(0, skel.position[i], Quaternion.identity, 0.005f, EventType.Repaint);
            }

            // Forearm stub, so which way the hand is facing is readable at a glance.
            Handles.DrawAAPolyLine(2f, Vector3.zero, new Vector3(0f, 0f, -0.12f));
        }

        private static void DrawGripAnnotation(Pose attach, XRGrabInteractable grab, string side)
        {
            Matrix4x4 restore = Handles.matrix;
            Color restoreColour = Handles.color;

            Handles.matrix = Matrix4x4.TRS(attach.position, attach.rotation, Vector3.one);

            const float axis = 0.045f;
            Handles.color = new Color(1f, 0.3f, 0.3f);
            Handles.DrawAAPolyLine(3f, Vector3.zero, Vector3.right * axis);      // X -> thumb
            Handles.color = new Color(0.4f, 1f, 0.4f);
            Handles.DrawAAPolyLine(3f, Vector3.zero, Vector3.up * axis);         // Y -> back of hand
            Handles.color = new Color(0.4f, 0.6f, 1f);
            Handles.DrawAAPolyLine(3f, Vector3.zero, Vector3.forward * axis);    // Z -> along fingers

            Handles.color = new Color(1f, 1f, 0.2f, 0.9f);
            Handles.SphereHandleCap(0, Vector3.zero, Quaternion.identity, 0.009f, EventType.Repaint);

            Handles.matrix = restore;

            if (HexRGripAttachPreviewSettings.ShowLabels)
            {
                Vector3 s = grab.transform.lossyScale;
                bool uneven = Mathf.Abs(s.x - s.y) > 1e-4f || Mathf.Abs(s.y - s.z) > 1e-4f;

                var byHand = grab.GetComponent<HexRGripByHand>();
                string mode = byHand != null
                    ? byHand.gripMode + (byHand.mirrorHands ? ", mirrored" : ", per-hand")
                    : "single grip";

                string text = grab.name + "  [" + side + "]  " + mode
                              + (HexRGripAttachPreviewSettings.HasCapturedFrame ? "\nmeasured frame" : "\nassumed frame")
                              + (uneven ? "\nnon-uniform scale -- local values will mislead" : string.Empty);

                Handles.color = Color.white;
                Handles.Label(attach.position + (Vector3.up * 0.03f), text, EditorStyles.helpBox);
            }

            Handles.color = restoreColour;
        }
    }
}
