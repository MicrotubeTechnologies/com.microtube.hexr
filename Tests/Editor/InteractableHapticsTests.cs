using HaptGlove;
using NUnit.Framework;

namespace HexR.Tests
{
    /// <summary>
    /// HexRInteractableHaptics, driven the way a backend drives it: through its
    /// IHexRInteractionSink, which is what XRI's and Meta's adapters call on grab and hover.
    /// </summary>
    public class InteractableHapticsTests
    {
        private TestRig rig;
        private HexRInteractableHaptics haptics;
        private readonly object interactor = new object();

        [SetUp]
        public void SetUp()
        {
            rig = new TestRig();
            // Hand-near is what the interactors report while grabbing.
            rig.LeftTracker.HandGrabbing = true;

            haptics = rig.Make("Prop").AddComponent<HexRInteractableHaptics>();
            haptics.leftPressureTracker = rig.LeftTracker;
            haptics.rightPressureTracker = rig.RightTracker;
            haptics.thumb = true;
            haptics.index = true;
            haptics.middle = true;
            haptics.ring = false;
            haptics.pinky = false;
            haptics.palm = false;
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
        }

        private void Grab()
        {
            ((IHexRInteractionSink)haptics).SetInteractorState(interactor, HexRHandSide.Left, false, true);
        }

        private Haptics.ChannelState Index()
        {
            return rig.Left.haptics.GetChannelState(Haptics.Finger.Index);
        }

        [TestCase(10f, 0.1f)]
        [TestCase(30f, 0.46f)]
        [TestCase(60f, 1f)]
        public void Strength_ReachesTheGloveOnItsOwnScale(float strength, float intensity)
        {
            haptics.followGrip = false;
            haptics.strength = strength;

            Grab();

            Assert.AreEqual(Haptics.HapticMode.Pressure, Index().Mode);
            Assert.AreEqual(intensity, Index().Intensity, 1e-4f);
        }

        [Test]
        public void FollowGrip_ScalesWithTheHandsFingerTracking()
        {
            haptics.followGrip = true;
            haptics.strength = 60f;
            haptics.minStrength = 10f;
            FingerUseTracking fingers = TestRig.FingersOf(rig.Left);

            // Half closed: lerp(10, 60, 0.5) = 35, stepped to 40.
            TestRig.SetFingers(fingers, 0.5f);
            Grab();
            Assert.AreEqual(PressureTrackerMain.StrengthToIntensity(40f), Index().Intensity, 1e-4f);

            // Nearly open: lerp(10, 60, 0.1) = 15, stepped to 20.
            TestRig.SetFingers(fingers, 0.9f);
            Private.Call(haptics, "Update");
            Assert.AreEqual(PressureTrackerMain.StrengthToIntensity(20f), Index().Intensity, 1e-4f);
        }

        [Test]
        public void Disabling_ReleasesTheChannelsItPressed()
        {
            haptics.followGrip = false;
            haptics.strength = 30f;
            Grab();
            Assume.That(Index().Mode, Is.EqualTo(Haptics.HapticMode.Pressure));

            Private.Call(haptics, "OnDisable");

            Assert.AreEqual(Haptics.HapticMode.Off, Index().Mode);
        }
    }
}
