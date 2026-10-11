using HaptGlove;
using NUnit.Framework;

namespace HexR.Tests
{
    /// <summary>
    /// The 10-60 strength the components show in the Inspector against the 0.1-1 intensity the
    /// glove calls take. Until 0.9.4 nothing converted between them, so every value from about 1
    /// upwards was clamped to 1 and pressed at full strength.
    /// </summary>
    public class StrengthScaleTests
    {
        [TestCase(10f, 0.1f, TestName = "10 is the glove's lowest pressure")]
        [TestCase(60f, 1f, TestName = "60 is the glove's highest pressure")]
        [TestCase(30f, 0.46f)]
        [TestCase(35f, 0.55f)]
        [TestCase(40f, 0.64f)]
        [TestCase(0f, 0.1f, TestName = "Below the scale clamps to the lowest")]
        [TestCase(100f, 1f, TestName = "Above the scale clamps to the highest")]
        public void StrengthToIntensity_MapsTheInspectorScaleOntoTheGloveRange(float strength, float intensity)
        {
            Assert.AreEqual(intensity, PressureTrackerMain.StrengthToIntensity(strength), 1e-4f);
        }

        [Test]
        public void PhysicalButton_PressesAtItsSetPressure_NotFull()
        {
            using (TestRig rig = new TestRig())
            {
                HexRPhysicalButton button = rig.Make("Button").AddComponent<HexRPhysicalButton>();
                button.pressPressure = 40f;
                button.travel = 0f;
                HapticFingerTrigger tip = rig.MakeFingertip("R_IndexTip", HapticFingerTrigger.HandType.Right,
                                                            HapticFingerTrigger.FingerType.Index);

                Private.Call(button, "OnTriggerEnter", tip.GetComponent<UnityEngine.Collider>());

                Haptics.ChannelState index = rig.Right.haptics.GetChannelState(Haptics.Finger.Index);
                Assert.AreEqual(Haptics.HapticMode.Pressure, index.Mode);
                Assert.AreEqual(0.64f, index.Intensity, 1e-4f);
            }
        }
    }
}
