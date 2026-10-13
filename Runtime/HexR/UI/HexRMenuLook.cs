using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HexR
{
    /// <summary>
    /// How the floating menu looks: the ARIS look shared with Microtube's other apps. Near-black
    /// glass edged in a thin white line, section labels as small spaced capitals behind a short
    /// orange rule, ghost pill buttons that turn solid orange for the one in use, Archivo for
    /// display type and Inter for reading text, and mint for a glove that is connected.
    ///
    /// The colours are composited onto the panel colour the way a browser would, in sRGB, and kept
    /// opaque. The look was drawn in CSS with translucent whites over black; blended by Unity in a
    /// linear-space project, 6% white over near-black comes out mid-grey instead of a faint lift.
    ///
    /// The fonts ship with the package (under SIL OFL 1.1, licences beside them) and are turned
    /// into TextMesh Pro font assets at runtime, so they work on whichever TextMesh Pro version the
    /// project has without depending on a font asset serialized by a different one. If they cannot
    /// be loaded the labels keep TextMesh Pro's default font.
    ///
    /// Corners come from one white rounded-rect texture made in code and nine-sliced, so any Image
    /// gets corners of any radius, filled or as a hairline ring, without an imported sprite.
    /// </summary>
    internal static class HexRMenuLook
    {
        // ---------------------------------------------------------------- colours

        private static readonly Color k_PanelRgb = new Color(9 / 255f, 10 / 255f, 14 / 255f, 1f);

        /// <summary>The panel. More opaque than ARIS's 86% glass: it is read at arm's length over a lit room.</summary>
        public static readonly Color Panel = new Color(k_PanelRgb.r, k_PanelRgb.g, k_PanelRgb.b, 0.94f);

        public static readonly Color Line = OnPanel(new Color(1f, 1f, 1f, 0.10f));
        public static readonly Color Line2 = OnPanel(new Color(1f, 1f, 1f, 0.18f));

        public static readonly Color Text = Color.white;
        public static readonly Color Muted = OnPanel(new Color(1f, 1f, 1f, 0.66f));
        public static readonly Color Faint = OnPanel(new Color(1f, 1f, 1f, 0.44f));

        /// <summary>A ghost button's face, and with a finger on it.</summary>
        public static readonly Color GhostFace = OnPanel(new Color(1f, 1f, 1f, 0.06f));
        public static readonly Color GhostHover = OnPanel(new Color(1f, 1f, 1f, 0.12f));
        public static readonly Color GhostPressed = OnPanel(new Color(0.949f, 0.4f, 0.102f, 0.28f));

        /// <summary>A haptics readout cell at rest.</summary>
        public static readonly Color CellFace = OnPanel(new Color(1f, 1f, 1f, 0.05f));

        public static readonly Color Accent = Hex(0xF2661A);
        public static readonly Color AccentHover = Hex(0xFF8A3D);
        public static readonly Color AccentEdge = WithAlpha(Hex(0xFF9D45), 0.6f);
        public static readonly Color AccentDeep = Hex(0xC84A0C);
        public static readonly Color Rim = Hex(0xFF7A2E);

        public static readonly Color Ok = Hex(0x52D6A0);
        public static readonly Color Warn = Hex(0xFF7A6B);

        /// <summary>Corner radius of the panel and hairline width, in panel pixels.</summary>
        public const float PanelRadius = 22f;
        public const float Stroke = 2f;
        public const float CellRadius = 7f;

        private static Color OnPanel(Color c)
        {
            float a = c.a;
            return new Color(
                c.r * a + k_PanelRgb.r * (1f - a),
                c.g * a + k_PanelRgb.g * (1f - a),
                c.b * a + k_PanelRgb.b * (1f - a),
                1f);
        }

        public static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, a);

        private static Color Hex(int rgb) =>
            new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

        // ---------------------------------------------------------------- fonts

        private const string k_FontFolder = "HexR/Fonts/";
        private static readonly Dictionary<string, TMP_FontAsset> fonts = new Dictionary<string, TMP_FontAsset>();

        /// <summary>Archivo ExtraBold: the wordmark.</summary>
        public static TMP_FontAsset Display => Font("Archivo-ExtraBold");

        /// <summary>Archivo Bold: section labels and column heads.</summary>
        public static TMP_FontAsset DisplayBold => Font("Archivo-Bold");

        /// <summary>Inter Medium: reading text.</summary>
        public static TMP_FontAsset Body => Font("Inter-Medium");

        /// <summary>Inter SemiBold: buttons.</summary>
        public static TMP_FontAsset BodyStrong => Font("Inter-SemiBold");

        private static TMP_FontAsset Font(string name)
        {
            // A failed load is remembered as null, so a missing font costs one lookup, not one per label.
            if (fonts.TryGetValue(name, out TMP_FontAsset cached))
            {
                return cached;
            }

            TMP_FontAsset asset = null;
            Font source = Resources.Load<Font>(k_FontFolder + name);
            if (source != null)
            {
                asset = TMP_FontAsset.CreateFontAsset(source);
            }

            if (asset != null)
            {
                asset.name = name + " (HexR menu)";
                // Made in memory and never saved, including from the edit-mode preview.
                asset.hideFlags = HideFlags.DontSave;
                if (asset.material != null)
                {
                    asset.material.hideFlags = HideFlags.DontSave;
                }

                if (asset.atlasTextures != null)
                {
                    foreach (Texture2D atlas in asset.atlasTextures)
                    {
                        if (atlas != null) atlas.hideFlags = HideFlags.DontSave;
                    }
                }
            }

            fonts[name] = asset;
            return asset;
        }

        /// <summary>Gives <paramref name="label"/> one of the menu's fonts, or leaves TextMesh Pro's default.</summary>
        public static void SetFont(TextMeshProUGUI label, TMP_FontAsset font)
        {
            if (font != null)
            {
                label.font = font;
            }
        }

        // ---------------------------------------------------------------- corners

        // A sliced corner is drawn at k_SpriteRadius / pixelsPerUnitMultiplier canvas pixels.
        private const int k_SpriteRadius = 32;

        private static Sprite fill;
        private static readonly Dictionary<int, Sprite> outlines = new Dictionary<int, Sprite>();

        /// <summary>
        /// Rounds <paramref name="image"/> to <paramref name="radius"/> canvas pixels: a filled
        /// shape, or a ring <paramref name="stroke"/> pixels wide inside its edge.
        /// </summary>
        public static void Round(Image image, float radius, bool filled, float stroke = Stroke)
        {
            radius = Mathf.Max(1f, radius);
            float multiplier = k_SpriteRadius / radius;
            image.sprite = filled ? Fill() : Outline(stroke * multiplier);
            image.type = Image.Type.Sliced;
            image.fillCenter = true;
            image.pixelsPerUnitMultiplier = multiplier;
        }

        private static Sprite Fill()
        {
            if (fill == null)
            {
                fill = Make(0f);
            }

            return fill;
        }

        // Keyed by stroke width in quarter texels, so every pill of the same height shares one.
        private static Sprite Outline(float width)
        {
            int key = Mathf.Max(1, Mathf.RoundToInt(width * 4f));
            if (!outlines.TryGetValue(key, out Sprite sprite) || sprite == null)
            {
                sprite = Make(key / 4f);
                outlines[key] = sprite;
            }

            return sprite;
        }

        // A white rounded square, anti-aliased, its corners a full k_SpriteRadius: filled when
        // strokeWidth is 0, otherwise a ring of that width inside the edge.
        private static Sprite Make(float strokeWidth)
        {
            const int r = k_SpriteRadius;
            const int size = r * 2 + 2;

            // Mipmapped: a 32-texel corner is drawn as small as 7 canvas pixels.
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = strokeWidth > 0f ? "HexR Menu Outline" : "HexR Menu Fill",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                hideFlags = HideFlags.DontSave,
            };

            Color32[] pixels = new Color32[size * size];
            float half = size * 0.5f;
            float inner = half - r;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Signed distance to a rounded square of half-size `half` and corner radius r.
                    float px = Mathf.Abs(x + 0.5f - half) - inner;
                    float py = Mathf.Abs(y + 0.5f - half) - inner;
                    float outside = new Vector2(Mathf.Max(px, 0f), Mathf.Max(py, 0f)).magnitude
                                  + Mathf.Min(Mathf.Max(px, py), 0f) - r;
                    float a = Mathf.Clamp01(0.5f - outside);
                    if (strokeWidth > 0f)
                    {
                        a *= Mathf.Clamp01(outside + strokeWidth + 0.5f);
                    }

                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(true, true);

            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(r, r, r, r));
            sprite.hideFlags = HideFlags.DontSave;
            return sprite;
        }
    }
}
