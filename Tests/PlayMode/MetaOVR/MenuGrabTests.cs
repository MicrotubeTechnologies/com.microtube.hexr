using NUnit.Framework;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using UnityEngine;

namespace HexR.Tests
{
    /// <summary>
    /// The floating menu's header is made grabbable at runtime. Until 0.9.5 the Meta backend left
    /// its HandGrabInteractable without a pointable element, so a hand grabbed the header and the
    /// menu stayed where it was.
    ///
    /// A play-mode test because the bug is invisible in edit mode: there, adding a
    /// HandGrabInteractable runs its Editor-only Reset, which makes the link itself.
    /// </summary>
    public class MenuGrabTests
    {
        private GameObject panel;

        [TearDown]
        public void TearDown()
        {
            if (panel != null) Object.Destroy(panel);
        }

        [Test]
        public void MakeGrabbable_HandsTheGrabbableToTheInteractable()
        {
            IHexRInteractionBackend backend = HexRInteractionBackends.Get(HexRManager.Options.MetaOVR);
            Assume.That(backend, Is.Not.Null, "The Meta backend registers on entering Play mode.");

            // As HexRFloatingMenu.AddGrab prepares the panel before handing it over.
            panel = new GameObject("Menu Panel");
            Rigidbody body = panel.AddComponent<Rigidbody>();
            body.isKinematic = true;
            panel.AddComponent<BoxCollider>();

            backend.MakeGrabbable(panel);

            Grabbable grabbable = panel.GetComponent<Grabbable>();
            HandGrabInteractable interactable = panel.GetComponent<HandGrabInteractable>();
            Assert.IsNotNull(grabbable);
            Assert.IsNotNull(interactable);
            Assert.AreSame(grabbable, interactable.PointableElement);
        }
    }
}
