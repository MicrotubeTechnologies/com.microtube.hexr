using HaptGlove;
using NUnit.Framework;
using UnityEngine;

namespace HexR.Tests
{
    /// <summary>
    /// HexRGrabbable: starting up as the Create Demo Scene cube does (no parent), and a palm grab
    /// reaching the right glove.
    /// </summary>
    public class GrabbableTests
    {
        private TestRig rig;

        [SetUp]
        public void SetUp()
        {
            rig = new TestRig();
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
        }

        private HexRGrabbable MakeCube()
        {
            GameObject cube = rig.Make("Grabbable Cube");
            cube.AddComponent<BoxCollider>().isTrigger = true;
            cube.AddComponent<Rigidbody>().useGravity = false;
            return cube.AddComponent<HexRGrabbable>();
        }

        [Test]
        public void Start_AtTheSceneRoot_DoesNotThrow()
        {
            HexRGrabbable cube = MakeCube();
            Assume.That(cube.transform.parent, Is.Null);

            Assert.DoesNotThrow(() => Private.Call(cube, "Start"));
        }

        [Test]
        public void Start_WithNoRigInTheScene_DoesNotThrow()
        {
            TestRig.SetInstance(null);
            HexRGrabbable cube = MakeCube();

            Assert.DoesNotThrow(() => Private.Call(cube, "Start"));
        }

        [Test]
        public void PalmGrab_PressesTheTouchingFingers_OnTheRightGlove()
        {
            HexRGrabbable cube = MakeCube();
            cube.TypeOfGrab = HexRGrabbable.Options.PalmGrab;
            cube.HapticStrength = 30f;
            int grabs = 0;
            // AddComponent leaves serialised UnityEvents null; a scene or prefab would build it.
            cube.OnGrab = new UnityEngine.Events.UnityEvent();
            cube.OnGrab.AddListener(() => grabs++);
            Private.Call(cube, "Start");

            // The palm and the index finger of the right hand close on it.
            Private.Call(cube, "SetFingerState", HapticFingerTrigger.FingerType.Palm, true, true);
            Private.Call(cube, "SetFingerState", HapticFingerTrigger.FingerType.Index, true, true);
            Assert.DoesNotThrow(() => Private.Call(cube, "Update"));

            Assert.AreEqual(1, grabs);
            foreach (Haptics.Finger channel in new[] { Haptics.Finger.Index, Haptics.Finger.Palm })
            {
                Haptics.ChannelState state = rig.Right.haptics.GetChannelState(channel);
                Assert.AreEqual(Haptics.HapticMode.Pressure, state.Mode, channel.ToString());
                Assert.AreEqual(PressureTrackerMain.StrengthToIntensity(30f), state.Intensity, 1e-4f, channel.ToString());
            }
            Assert.AreEqual(Haptics.HapticMode.Off, rig.Right.haptics.GetChannelState(Haptics.Finger.Thumb).Mode);
            Assert.AreEqual(Haptics.HapticMode.Off, rig.Left.haptics.GetChannelState(Haptics.Finger.Index).Mode);
        }

        [Test]
        public void Disabling_MidGrab_ReleasesTheGlove()
        {
            HexRGrabbable cube = MakeCube();
            cube.TypeOfGrab = HexRGrabbable.Options.PalmGrab;
            cube.HapticStrength = 30f;
            Private.Call(cube, "Start");
            Private.Call(cube, "SetFingerState", HapticFingerTrigger.FingerType.Palm, true, true);
            Private.Call(cube, "SetFingerState", HapticFingerTrigger.FingerType.Index, true, true);
            Private.Call(cube, "Update");
            Assume.That(rig.Right.haptics.GetChannelState(Haptics.Finger.Index).Mode, Is.EqualTo(Haptics.HapticMode.Pressure));

            Private.Call(cube, "OnDisable");

            Assert.AreEqual(Haptics.HapticMode.Off, rig.Right.haptics.GetChannelState(Haptics.Finger.Index).Mode);
            Assert.AreEqual(Haptics.HapticMode.Off, rig.Right.haptics.GetChannelState(Haptics.Finger.Palm).Mode);
        }
    }
}
