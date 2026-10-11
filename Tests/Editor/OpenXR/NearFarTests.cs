using HexR.OpenXR;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace HexR.Tests
{
    /// <summary>
    /// XRI 3's hand rig (XR Origin Hands) grabs with a Near-Far Interactor and has no XR Direct
    /// Interactor. Until 0.9.4 Auto Setup looked for the Direct one only, so on that rig grab
    /// gating was never wired.
    /// </summary>
    public class NearFarTests
    {
        private GameObject hand, controller;

        [TearDown]
        public void TearDown()
        {
            if (hand != null) Object.DestroyImmediate(hand);
            if (controller != null) Object.DestroyImmediate(controller);
        }

        private T Child<T>(string name) where T : Component
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(hand.transform, false);
            return go.AddComponent<T>();
        }

        [Test]
        public void AutoFind_TakesTheNearFarInteractor_WhenThereIsNoDirectOne()
        {
            hand = new GameObject("Left Hand");
            NearFarInteractor nearFar = Child<NearFarInteractor>("Near-Far Interactor");
            XRPokeInteractor poke = Child<XRPokeInteractor>("Poke Interactor");

            controller = new GameObject("Left Pressure Controller");
            PressureTrackerMain tracker = controller.AddComponent<PressureTrackerMain>();
            OpenXRHandNearSource source = controller.AddComponent<OpenXRHandNearSource>();

            Assert.IsTrue(source.AutoFind(hand.transform, "Left"));
            Assert.AreSame(nearFar, tracker.handGrabInteractor);
            Assert.AreSame(poke, tracker.pokeInteractor);
        }

        [Test]
        public void AutoFind_StillPrefersTheDirectInteractor()
        {
            hand = new GameObject("Left Hand");
            XRDirectInteractor direct = Child<XRDirectInteractor>("Direct Interactor");
            Child<NearFarInteractor>("Near-Far Interactor");
            Child<XRPokeInteractor>("Poke Interactor");

            controller = new GameObject("Left Pressure Controller");
            PressureTrackerMain tracker = controller.AddComponent<PressureTrackerMain>();
            OpenXRHandNearSource source = controller.AddComponent<OpenXRHandNearSource>();

            Assert.IsTrue(source.AutoFind(hand.transform, "Left"));
            Assert.AreSame(direct, tracker.handGrabInteractor);
        }
    }
}
