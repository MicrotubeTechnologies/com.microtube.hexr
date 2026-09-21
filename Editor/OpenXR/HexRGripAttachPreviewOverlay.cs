using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Scene-view controls for <see cref="HexRGripAttachPreview"/>.
///
/// Overlay placement and visibility live in UserSettings/Layouts, so like the rest of the tool
/// this adds nothing to the project's files.
/// </summary>

namespace HexR.OpenXR
{
    [Overlay(typeof(SceneView), "hexr-grip-attach-preview", "Grip Attach Preview")]
    internal class HexRGripAttachPreviewOverlay : Overlay
    {
        private ObjectField wristField;
        private ObjectField attachField;
        private Label readout;

        public override VisualElement CreatePanelContent()
        {
            var root = new VisualElement { style = { minWidth = 260f } };

            var enabled = new Toggle("Show preview") { value = HexRGripAttachPreviewSettings.Enabled };
            enabled.RegisterValueChangedCallback(e =>
            {
                HexRGripAttachPreviewSettings.Enabled = e.newValue;
                if (e.newValue)
                {
                    HexRGripAttachPreview.Subscribe();
                }
                else
                {
                    HexRGripAttachPreview.Unsubscribe();
                }

                SceneView.RepaintAll();
            });
            root.Add(enabled);

            var hands = new EnumField("Hands", HexRGripAttachPreviewSettings.Hands);
            hands.RegisterValueChangedCallback(e =>
            {
                HexRGripAttachPreviewSettings.Hands = (HexRGripAttachPreviewSettings.HandsShown)e.newValue;
                SceneView.RepaintAll();
            });
            root.Add(hands);

            var opacity = new Slider("Opacity", 0.1f, 1f) { value = HexRGripAttachPreviewSettings.Opacity };
            opacity.RegisterValueChangedCallback(e =>
            {
                HexRGripAttachPreviewSettings.Opacity = e.newValue;
                SceneView.RepaintAll();
            });
            root.Add(opacity);

            var labels = new Toggle("Labels") { value = HexRGripAttachPreviewSettings.ShowLabels };
            labels.RegisterValueChangedCallback(e =>
            {
                HexRGripAttachPreviewSettings.ShowLabels = e.newValue;
                SceneView.RepaintAll();
            });
            root.Add(labels);

            readout = new Label { style = { whiteSpace = WhiteSpace.Normal, marginTop = 4f, marginBottom = 4f } };
            root.Add(readout);

            var worn = new Button(ViewAsWorn) { text = "View as worn" };
            root.Add(worn);

            root.Add(BuildCapturePanel());

            root.schedule.Execute(RefreshReadout).Every(200);
            RefreshReadout();
            return root;
        }

        private VisualElement BuildCapturePanel()
        {
            var box = new VisualElement
            {
                style =
                {
                    marginTop = 6f, paddingTop = 4f, paddingBottom = 4f,
                    borderTopWidth = 1f, borderTopColor = new StyleColor(new Color(0f, 0f, 0f, 0.3f)),
                },
            };

            box.Add(new Label("Measure the real attach frame")
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold },
            });

            box.Add(new Label("The preview assumes the interactor's attach sits at the pinch point with " +
                              "the wrist's rotation. In Play mode, drop the hand's wrist bone and the " +
                              "interactor's attach transform here and capture to replace that assumption " +
                              "with a measurement.")
            {
                style = { whiteSpace = WhiteSpace.Normal, fontSize = 10f, opacity = 0.8f },
            });

            wristField = new ObjectField("Wrist") { objectType = typeof(Transform), allowSceneObjects = true };
            attachField = new ObjectField("Interactor attach") { objectType = typeof(Transform), allowSceneObjects = true };
            box.Add(wristField);
            box.Add(attachField);

            box.Add(new Button(Capture) { text = "Capture attach frame" });
            box.Add(new Button(() =>
            {
                HexRGripAttachPreviewSettings.ClearCapture();
                RefreshReadout();
                SceneView.RepaintAll();
            })
            { text = "Reset to assumed frame" });

            return box;
        }

        private void Capture()
        {
            var wrist = wristField.value as Transform;
            var attach = attachField.value as Transform;
            if (wrist == null || attach == null)
            {
                Debug.LogWarning("[HexR] Set both Wrist and Interactor attach before capturing.");
                return;
            }

            Vector3 local = wrist.InverseTransformPoint(attach.position);
            Quaternion rot = Quaternion.Inverse(wrist.rotation) * attach.rotation;

            // Stored for the left hand; the right is mirrored in X when drawn, because the hand
            // frame's X axis points at the thumb and that flips between hands.
            if (wrist.name.StartsWith("R_"))
            {
                local.x = -local.x;
            }

            HexRGripAttachPreviewSettings.CapturedPosition = local;
            HexRGripAttachPreviewSettings.CapturedEuler = rot.eulerAngles;
            HexRGripAttachPreviewSettings.HasCapturedFrame = true;

            Debug.Log("[HexR] Captured attach frame: wrist-local pos " + local.ToString("F4")
                      + ", rot " + rot.eulerAngles.ToString("F1")
                      + " (assumed was pos ~(0.0467, -0.0489, 0.1393), rot (0,0,0)).");

            RefreshReadout();
            SceneView.RepaintAll();
        }

        private static void ViewAsWorn()
        {
            Transform attach = CurrentAttach();
            if (attach == null)
            {
                return;
            }

            // Roughly where the wearer's eyes would be relative to the grip: back along the fingers
            // and a little above.
            Vector3 eye = attach.position + (attach.rotation * new Vector3(0f, 0.15f, -0.45f));
            SceneView view = SceneView.lastActiveSceneView;
            if (view != null)
            {
                view.LookAt(attach.position, Quaternion.LookRotation(attach.position - eye), 0.5f);
            }
        }

        private static Transform CurrentAttach()
        {
            foreach (Transform t in Selection.transforms)
            {
                var grab = t.GetComponentInParent<XRGrabInteractable>(true);
                if (grab != null && !grab.useDynamicAttach && grab.attachTransform != null)
                {
                    return grab.attachTransform;
                }
            }

            return null;
        }

        private void RefreshReadout()
        {
            if (readout == null)
            {
                return;
            }

            string frame = HexRGripAttachPreviewSettings.HasCapturedFrame
                ? "frame: measured " + HexRGripAttachPreviewSettings.CapturedPosition.ToString("F4")
                : "frame: assumed (pinch point, wrist rotation)";

            foreach (Transform t in Selection.transforms)
            {
                var grab = t.GetComponentInParent<XRGrabInteractable>(true);
                if (grab == null)
                {
                    continue;
                }

                if (grab.useDynamicAttach)
                {
                    readout.text = grab.name + ": uses dynamic attach — nothing to preview.\n" + frame;
                    return;
                }

                if (grab.attachTransform == null)
                {
                    readout.text = grab.name + ": no attach transform assigned.\n" + frame;
                    return;
                }

                readout.text = grab.name + " / " + grab.attachTransform.name
                               + "\npos " + grab.attachTransform.localPosition.ToString("F3")
                               + "  rot " + grab.attachTransform.localEulerAngles.ToString("F1")
                               + "\n" + frame;
                return;
            }

            readout.text = "Select a grabbable (or its Grip Attach).\n" + frame;
        }
    }
}
