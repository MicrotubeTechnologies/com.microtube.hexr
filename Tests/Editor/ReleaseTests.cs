using HaptGlove;
using NUnit.Framework;

namespace HexR.Tests
{
    /// <summary>
    /// Releasing the glove when the app stops talking to it. The glove keeps its last command
    /// until told otherwise, so before 0.9.4 quitting -- including stopping Play mode -- left it
    /// pressing.
    ///
    /// What these check is that the release commands are built for every channel, read back
    /// from the per-channel record each command updates. Whether the glove receives them is a
    /// hardware test; there is no Bluetooth link here, and the write itself fails quietly.
    /// </summary>
    public class ReleaseTests
    {
        private TestRig rig;

        [SetUp]
        public void SetUp()
        {
            rig = new TestRig();
            rig.Left.bleConnected = true;
            // Something pressing and something buzzing.
            rig.Left.haptics.HEXRPressure(Haptics.Finger.Index, true, 0.5f, 1f);
            rig.Left.haptics.HEXRVibration(Haptics.Finger.Palm, true, 10f, 0.5f);
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
        }

        private void AssertAllOff()
        {
            foreach (Haptics.Finger channel in System.Enum.GetValues(typeof(Haptics.Finger)))
            {
                Assert.AreEqual(Haptics.HapticMode.Off, rig.Left.haptics.GetChannelState(channel).Mode, channel.ToString());
            }
        }

        [Test]
        public void ReleaseAllChannels_SwitchesPressureAndVibrationOff()
        {
            rig.Left.ReleaseAllChannels();
            AssertAllOff();
        }

        [Test]
        public void Quitting_ReleasesTheGlove()
        {
            Assert.DoesNotThrow(() => Private.Call(rig.Left, "OnApplicationQuit"));
            AssertAllOff();
        }

        [Test]
        public void Pausing_ReleasesTheGlove()
        {
            Private.Call(rig.Left, "OnApplicationPause", true);
            AssertAllOff();
        }

        [Test]
        public void Pausing_WithReleaseWhenPausedOff_LeavesItAlone()
        {
            rig.Left.releaseWhenPaused = false;
            Private.Call(rig.Left, "OnApplicationPause", true);
            Assert.AreEqual(Haptics.HapticMode.Pressure, rig.Left.haptics.GetChannelState(Haptics.Finger.Index).Mode);
        }

        [Test]
        public void ReleaseChannels_TouchesOnlyThoseChannels()
        {
            rig.Left.ReleaseChannels(Haptics.Finger.Palm);
            Assert.AreEqual(Haptics.HapticMode.Off, rig.Left.haptics.GetChannelState(Haptics.Finger.Palm).Mode);
            Assert.AreEqual(Haptics.HapticMode.Pressure, rig.Left.haptics.GetChannelState(Haptics.Finger.Index).Mode);
        }

        [Test]
        public void SpecialHaptics_Disabled_ReleasesOnlyWhatTheZoneApplied()
        {
            rig.Right.bleConnected = true;
            SpecialHaptics zone = rig.Make("Zone").AddComponent<SpecialHaptics>();
            zone.TypeOfHaptics = SpecialHaptics.Options.CustomVibrations;
            zone.RPressureTracker = rig.RightTracker;
            zone.LPressureTracker = rig.LeftTracker;
            Private.Call(zone, "Start");

            // The zone buzzes the right thumb; something else is pressing the right index.
            Private.Set(zone, "Thumb_Bool", true);
            Private.Set(zone, "Right_Bool", true);
            Private.Call(zone, "TriggerHapticForVibrations", rig.Right);
            rig.Right.haptics.HEXRPressure(Haptics.Finger.Index, true, 0.5f, 1f);
            Assume.That(rig.Right.haptics.GetChannelState(Haptics.Finger.Thumb).Mode, Is.EqualTo(Haptics.HapticMode.Vibration));

            Private.Call(zone, "OnDisable");

            Assert.AreEqual(Haptics.HapticMode.Off, rig.Right.haptics.GetChannelState(Haptics.Finger.Thumb).Mode);
            Assert.AreEqual(Haptics.HapticMode.Pressure, rig.Right.haptics.GetChannelState(Haptics.Finger.Index).Mode);
        }

        [Test]
        public void SpecialHaptics_CustomHaptics_PressesAtItsSetPressure_NotFull()
        {
            rig.Right.bleConnected = true;
            SpecialHaptics zone = rig.Make("Zone").AddComponent<SpecialHaptics>();
            zone.TypeOfHaptics = SpecialHaptics.Options.CustomHaptics;
            zone.HapticPressure = 30f;
            zone.RPressureTracker = rig.RightTracker;
            zone.LPressureTracker = rig.LeftTracker;
            Private.Call(zone, "Start");
            Private.Set(zone, "timer", 0f);
            HapticFingerTrigger tip = rig.MakeFingertip("XRHand_IndexTip", HapticFingerTrigger.HandType.Right,
                                                        HapticFingerTrigger.FingerType.Index);

            Private.Call(zone, "OnTriggerEnter", tip.GetComponent<UnityEngine.Collider>());

            Haptics.ChannelState index = rig.Right.haptics.GetChannelState(Haptics.Finger.Index);
            Assert.AreEqual(Haptics.HapticMode.Pressure, index.Mode);
            Assert.AreEqual(PressureTrackerMain.StrengthToIntensity(30f), index.Intensity, 1e-4f);
        }
    }
}
