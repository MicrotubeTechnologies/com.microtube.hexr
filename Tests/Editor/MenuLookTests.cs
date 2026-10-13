using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace HexR.Tests
{
    /// <summary>
    /// The floating menu's look, from 0.9.6. Its fonts ship with the package rather than being
    /// whatever the project's TextMesh Pro default is, and each button draws its own state --
    /// ghost, in use, pressed -- from its HexRMenuButton, so a project that copies a button gets
    /// the same look and press feedback.
    /// </summary>
    public class MenuLookTests
    {
        private GameObject button;

        [TearDown]
        public void TearDown()
        {
            if (button != null)
            {
                Object.DestroyImmediate(button);
            }
        }

        [Test]
        public void Fonts_ShipWithThePackage()
        {
            Assert.IsNotNull(HexRMenuLook.Display, "Archivo ExtraBold");
            Assert.IsNotNull(HexRMenuLook.DisplayBold, "Archivo Bold");
            Assert.IsNotNull(HexRMenuLook.Body, "Inter Medium");
            Assert.IsNotNull(HexRMenuLook.BodyStrong, "Inter SemiBold");
        }

        [Test]
        public void Button_IsAGhostUntilItIsTheOneInUse()
        {
            HexRMenuButton look = MakeButton(out Image face, out _);
            Assert.AreEqual(HexRMenuLook.GhostFace, face.color);

            look.Active = true;
            Assert.AreEqual(HexRMenuLook.Accent, face.color);
        }

        [Test]
        public void Button_DarkensWhilePressed()
        {
            MakeButton(out Image face, out HexRPhysicalButton press);

            press.onPressed.Invoke();
            Assert.AreEqual(HexRMenuLook.GhostPressed, face.color);

            press.onReleased.Invoke();
            Assert.AreEqual(HexRMenuLook.GhostFace, face.color);
        }

        // As HexRFloatingMenu builds one: the face, its edge, the press volume, then the look.
        private HexRMenuButton MakeButton(out Image face, out HexRPhysicalButton press)
        {
            button = new GameObject("Button", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            face = button.GetComponent<Image>();

            GameObject outline = new GameObject("Outline", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            outline.transform.SetParent(button.transform, false);

            press = button.AddComponent<HexRPhysicalButton>();
            HexRMenuButton look = button.AddComponent<HexRMenuButton>();
            look.Init(face, outline.GetComponent<Image>(), false);
            return look;
        }
    }
}
