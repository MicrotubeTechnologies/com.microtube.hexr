using NUnit.Framework;
using UnityEngine;

namespace HexR.Tests
{
    /// <summary>
    /// HexRUsable telling the hands apart, with Meta's joint names, which carry no L_ / R_.
    /// </summary>
    public class UsableTests
    {
        private TestRig rig;
        private HexRUsable usable;
        private Collider metaIndexTip;
        private int uses, stops;

        [SetUp]
        public void SetUp()
        {
            rig = new TestRig();
            usable = rig.Make("Torch").AddComponent<HexRUsable>();
            usable.Thumb = HexRUsable.Options.NotNeeded;
            usable.IndexFinger = HexRUsable.Options.Needed;
            usable.MiddleFinger = HexRUsable.Options.NotNeeded;
            usable.RingFinger = HexRUsable.Options.NotNeeded;
            usable.LittleFinger = HexRUsable.Options.NotNeeded;
            usable.UseThreshold = 0.3f;
            uses = stops = 0;
            usable.WhenUseTriggerEvents = new UnityEngine.Events.UnityEvent();
            usable.WhenStopUsingTriggerEvents = new UnityEngine.Events.UnityEvent();
            usable.WhenUseTriggerEvents.AddListener(() => uses++);
            usable.WhenStopUsingTriggerEvents.AddListener(() => stops++);
            Private.Call(usable, "Start");

            metaIndexTip = rig.MakeFingertip("XRHand_IndexTip", HapticFingerTrigger.HandType.Right,
                                             HapticFingerTrigger.FingerType.Index).GetComponent<Collider>();
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
        }

        [Test]
        public void ATouch_IsMatchedToItsHand_WithMetaJointNames()
        {
            Private.Call(usable, "OnTriggerEnter", metaIndexTip);

            Assert.AreSame(TestRig.FingersOf(rig.Right),
                           Private.Get<FingerUseTracking>(usable, "Currentfingerusetracking"));
        }

        [Test]
        public void CurlingAndOpening_FiresUseThenStop_Once_Each()
        {
            FingerUseTracking fingers = TestRig.FingersOf(rig.Right);

            fingers.IndexUse = 0.1f; // curled
            Private.Call(usable, "OnTriggerEnter", metaIndexTip);
            Assert.DoesNotThrow(() => Private.Call(usable, "Update"));
            Assert.AreEqual(1, uses);
            Assert.AreEqual(0, stops);

            fingers.IndexUse = 0.9f; // open again
            Private.Call(usable, "OnTriggerStay", metaIndexTip);
            Private.Call(usable, "Update");
            Assert.AreEqual(1, uses);
            Assert.AreEqual(1, stops);
        }

        [Test]
        public void AnOpenHand_DoesNotFireStop_WithoutAUseFirst()
        {
            TestRig.FingersOf(rig.Right).IndexUse = 0.9f;
            Private.Call(usable, "OnTriggerEnter", metaIndexTip);
            Private.Call(usable, "Update");
            Private.Call(usable, "OnTriggerStay", metaIndexTip);
            Private.Call(usable, "Update");

            Assert.AreEqual(0, uses);
            Assert.AreEqual(0, stops);
        }
    }
}
