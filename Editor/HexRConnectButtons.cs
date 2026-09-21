using HaptGlove;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HexR
{
    /// <summary>
    /// Builds a connect panel you press with a finger: three physical buttons on a backing plate,
    /// wired to connect each glove and to show the hand colliders.
    ///
    /// This is the fallback for when a canvas menu will not do. A canvas needs a raycaster, an
    /// input module and an interactor that emits a poke or a ray, and all three differ per backend
    /// -- when any one is missing the panel is visible and simply does not respond, which is the
    /// hardest kind of broken to diagnose. These buttons are trigger colliders and HexR's own
    /// fingertips, so there is nothing backend-specific to be missing.
    ///
    /// Built from primitives rather than shipped as a prefab, for the same reason Create Demo Scene
    /// is: nothing is serialised into the package, so there is no prefab to go missing, no material
    /// pointing at a shader we do not ship, and no GUID to break. That is exactly how the old
    /// HexR Panel prefab failed.
    /// </summary>
    public static class HexRConnectButtons
    {
        private const float ButtonWidth = 0.05f;
        private const float ButtonHeight = 0.035f;
        private const float ButtonDepth = 0.014f;
        private const float Gap = 0.012f;

        [MenuItem("HexR/Add Connect Buttons", false, 24)]
        private static void AddConnectButtons()
        {
            HexRManager manager = HexRCompat.FindAny<HexRManager>();
            if (manager == null)
            {
                Debug.LogWarning("[HexR] No HexR rig in this scene -- the buttons need one to connect to. "
                                 + "Run HexR > Create HexR Rig first.");
                return;
            }

            GameObject root = new GameObject("HexR Connect Buttons");
            Undo.RegisterCreatedObjectUndo(root, "Add Connect Buttons");

            // Within arm's reach of someone standing at the rig, angled up slightly so it reads as
            // a console rather than a wall.
            root.transform.position = manager.transform.position + new Vector3(0f, 1.05f, 0.32f);
            root.transform.rotation = Quaternion.Euler(20f, 0f, 0f);

            float span = ButtonWidth * 3f + Gap * 2f;
            GameObject plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plate.name = "Plate";
            plate.transform.SetParent(root.transform, false);
            plate.transform.localScale = new Vector3(span + 0.02f, ButtonHeight + 0.02f, 0.008f);
            plate.transform.localPosition = new Vector3(0f, 0f, 0.008f);
            // The plate is scenery. A solid collider here would block the fingertip before it ever
            // reached a button.
            Object.DestroyImmediate(plate.GetComponent<Collider>());
            Paint(plate, new Color(0.10f, 0.11f, 0.13f));

            float x = -(span - ButtonWidth) * 0.5f;
            HaptGloveCollidersVisualizer visualizer = EnsureVisualizer(manager);

            Build(root.transform, "Connect Left", new Vector3(x, 0f, 0f),
                  new Color(0.17f, 0.20f, 0.26f), manager.ConnectLeftBT);
            Build(root.transform, "Connect Right", new Vector3(x + ButtonWidth + Gap, 0f, 0f),
                  new Color(0.17f, 0.20f, 0.26f), manager.ConnectRightBT);
            Build(root.transform, "Colliders", new Vector3(x + (ButtonWidth + Gap) * 2f, 0f, 0f),
                  new Color(0.20f, 0.17f, 0.13f), visualizer.ColliderToggle);

            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(root);
            EditorSceneManager.MarkSceneDirty(root.scene);
            Debug.Log("[HexR] Added a physical connect panel. Press the buttons with an index "
                      + "fingertip -- they need no canvas, raycaster or interactor, so they work the "
                      + "same on Meta and OpenXR.");
        }

        private static HaptGloveCollidersVisualizer EnsureVisualizer(HexRManager manager)
        {
            HaptGloveCollidersVisualizer v = manager.GetComponent<HaptGloveCollidersVisualizer>();
            return v != null ? v : Undo.AddComponent<HaptGloveCollidersVisualizer>(manager.gameObject);
        }

        private static void Build(Transform parent, string label, Vector3 localPos, Color tint,
                                  UnityEngine.Events.UnityAction action)
        {
            GameObject button = GameObject.CreatePrimitive(PrimitiveType.Cube);
            button.name = label;
            button.transform.SetParent(parent, false);
            button.transform.localPosition = localPos;
            button.transform.localScale = new Vector3(ButtonWidth, ButtonHeight, ButtonDepth);
            Paint(button, tint);

            // A trigger, because the fingertip has to pass through it rather than collide with it.
            // The fingertips already carry the Rigidbody the trigger callbacks need.
            button.GetComponent<BoxCollider>().isTrigger = true;

            HexRPhysicalButton press = button.AddComponent<HexRPhysicalButton>();
            press.travel = ButtonDepth * 0.45f;
            UnityEventTools.AddPersistentListener(press.onPressed, action);

            GameObject text = new GameObject("Label");
            text.transform.SetParent(button.transform, false);
            // Undo the button's non-uniform scale so the text is not squashed with it.
            text.transform.localScale = new Vector3(1f / ButtonWidth, 1f / ButtonHeight, 1f / ButtonDepth) * 0.01f;
            text.transform.localPosition = new Vector3(0f, 0f, -0.55f);

            TextMeshPro tmp = text.AddComponent<TextMeshPro>();
            tmp.text = label;
            tmp.fontSize = 3.2f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.94f, 0.95f, 0.97f);
            tmp.rectTransform.sizeDelta = new Vector2(4.5f, 3f);
        }

        // Shader.Find rather than the primitive's default material: the built-in default renders
        // magenta under URP, which is how the old HexR Panel prefab's buttons looked in half the
        // projects that opened it.
        private static void Paint(GameObject target, Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null)
            {
                return;
            }

            Material mat = new Material(shader) { name = target.name + " Material" };
            mat.color = color;
            target.GetComponent<Renderer>().sharedMaterial = mat;
        }
    }
}
