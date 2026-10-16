using System.Collections;
using NUnit.Framework;
using Oculus.Interaction;
using Oculus.Interaction.Grab;
using Oculus.Interaction.HandGrab;
using UnityEngine;
using UnityEngine.TestTools;

namespace HexR.Tests
{
    /// <summary>
    /// The floating menu's header is made grabbable at runtime. Until 0.9.5 the Meta backend left
    /// its HandGrabInteractable without a pointable element, so a hand grabbed the header and the
    /// menu stayed where it was. Until 0.10.3 the interactable listed every collider under the
    /// menu, the panel's buttons included, and the panel's rebuilds left that list holding
    /// destroyed colliders that Meta's grab scoring threw on.
    ///
    /// Play-mode tests because both bugs are invisible in edit mode: there, adding a
    /// HandGrabInteractable runs its Editor-only Reset, which makes the link itself, and its Start
    /// (where it lists the colliders) never runs.
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
            IHexRInteractionBackend backend = MetaBackend();

            panel = MakePanel();
            backend.MakeGrabbable(panel);

            Grabbable grabbable = panel.GetComponent<Grabbable>();
            HandGrabInteractable interactable = panel.GetComponent<HandGrabInteractable>();
            Assert.IsNotNull(grabbable);
            Assert.IsNotNull(interactable);
            Assert.AreSame(grabbable, interactable.PointableElement);
        }

        [UnityTest]
        public IEnumerator MakeGrabbable_GrabsByTheHeaderAlone_AndSurvivesAPanelRebuild()
        {
            IHexRInteractionBackend backend = MetaBackend();

            // A button's press volume under the menu, as the built panel has.
            panel = MakePanel();
            GameObject button = new GameObject("Button");
            button.transform.SetParent(panel.transform, false);
            button.AddComponent<BoxCollider>().isTrigger = true;

            backend.MakeGrabbable(panel);
            yield return null; // The interactable lists its colliders in Start.

            // The panel is rebuilt: the old button goes.
            Object.Destroy(button);
            yield return null;

            HandGrabInteractable interactable = panel.GetComponent<HandGrabInteractable>();
            Assert.AreEqual(1, interactable.Colliders.Length, "Only the header is grabbable.");
            Assert.IsTrue(interactable.Colliders[0] != null);
            Assert.AreEqual("Grab Handle", interactable.Colliders[0].gameObject.name);
            Assert.IsFalse(panel.GetComponent<BoxCollider>().enabled, "The header has one collider, not two.");

            // What a hand coming near the header does first. It threw on the destroyed button.
            Vector3 header = panel.transform.position;
            Assert.DoesNotThrow(() => GrabPoseHelper.CollidersScore(header, interactable.Colliders, out _));
        }

        private static IHexRInteractionBackend MetaBackend()
        {
            IHexRInteractionBackend backend = HexRInteractionBackends.Get(HexRManager.Options.MetaOVR);
            Assume.That(backend, Is.Not.Null, "The Meta backend registers on entering Play mode.");
            return backend;
        }

        // As HexRFloatingMenu.AddGrab prepares the panel before handing it over.
        private static GameObject MakePanel()
        {
            GameObject made = new GameObject("Menu Panel");
            Rigidbody body = made.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            made.AddComponent<BoxCollider>();
            return made;
        }
    }
}
