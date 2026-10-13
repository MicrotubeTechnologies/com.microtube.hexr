using UnityEngine;
using UnityEngine.UI;

namespace HexR
{
    /// <summary>
    /// One button on the floating menu, drawn in the menu's look (<see cref="HexRMenuLook"/>): a
    /// ghost pill with a hairline edge normally, solid orange when it is the one in use -- the
    /// scene you are in, the demo that is showing -- and darker while a fingertip is pressing it.
    ///
    /// The state lives on the button rather than in the menu so that a copy of the button keeps
    /// it: a project that adds its own button by duplicating one of the menu's gets the same look
    /// and the same press feedback without repeating any of this. The press feedback is hooked up
    /// in Awake for the same reason, since a copy does not inherit listeners added in code.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public class HexRMenuButton : MonoBehaviour
    {
        [SerializeField] private Image face;
        [SerializeField] private Image outline;
        [SerializeField] private bool active;

        private bool pressed;
        private HexRPhysicalButton press;

        /// <summary>Drawn solid orange: the scene or demo this button stands for is the current one.</summary>
        public bool Active
        {
            get => active;
            set
            {
                if (active == value)
                {
                    return;
                }

                active = value;
                Paint();
            }
        }

        internal void Init(Image buttonFace, Image buttonOutline, bool isActive)
        {
            face = buttonFace;
            outline = buttonOutline;
            active = isActive;
            Hook();
            Paint();
        }

        private void Awake()
        {
            Hook();
            Paint();
        }

        private void OnDestroy()
        {
            if (press != null)
            {
                press.onPressed.RemoveListener(OnPressed);
                press.onReleased.RemoveListener(OnReleased);
            }
        }

        private void Hook()
        {
            if (press != null)
            {
                return;
            }

            press = GetComponent<HexRPhysicalButton>();
            if (press != null)
            {
                press.onPressed.AddListener(OnPressed);
                press.onReleased.AddListener(OnReleased);
            }
        }

        private void OnPressed()
        {
            pressed = true;
            Paint();
        }

        private void OnReleased()
        {
            pressed = false;
            Paint();
        }

        private void Paint()
        {
            if (face == null)
            {
                return;
            }

            Color fill = active ? HexRMenuLook.Accent : HexRMenuLook.GhostFace;
            Color edge = active ? HexRMenuLook.AccentEdge : HexRMenuLook.Line2;
            Color hover = active ? HexRMenuLook.AccentHover : HexRMenuLook.GhostHover;
            Color down = active ? HexRMenuLook.AccentDeep : HexRMenuLook.GhostPressed;
            if (pressed)
            {
                fill = down;
                edge = HexRMenuLook.Rim;
            }

            face.color = fill;
            if (outline != null)
            {
                outline.color = edge;
            }

            // For a pointer pressing it through uGUI instead of a fingertip. ColorTint multiplies
            // the face's own colour, so its states are written relative to it. Disabled is left
            // untinted: the button the menu disables is the scene you are in, which should read as
            // the current one, not as greyed out.
            if (TryGetComponent(out Button button))
            {
                ColorBlock colors = button.colors;
                colors.normalColor = Color.white;
                colors.highlightedColor = Relative(hover, fill);
                colors.pressedColor = Relative(down, fill);
                colors.selectedColor = Color.white;
                colors.disabledColor = Color.white;
                colors.fadeDuration = 0.05f;
                button.colors = colors;
            }
        }

        private static Color Relative(Color wanted, Color baseColor)
        {
            return new Color(
                baseColor.r > 0.001f ? wanted.r / baseColor.r : 1f,
                baseColor.g > 0.001f ? wanted.g / baseColor.g : 1f,
                baseColor.b > 0.001f ? wanted.b / baseColor.b : 1f,
                1f);
        }
    }
}
