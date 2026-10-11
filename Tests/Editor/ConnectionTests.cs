using HaptGlove;
using NUnit.Framework;

namespace HexR.Tests
{
    /// <summary>
    /// Connecting from code with no floating menu in the scene, so no status labels.
    ///
    /// These stop short of Bluetooth: in edit mode the connect coroutine runs only to its first
    /// yield, before it reaches the handler, so they cover the manager's side of the connect and
    /// nothing below it.
    /// </summary>
    public class ConnectionTests
    {
        private TestRig rig;

        [SetUp]
        public void SetUp()
        {
            rig = new TestRig();
            Assume.That(rig.Manager.LeftBtText, Is.Null);
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
        }

        [Test]
        public void ConnectLeftBT_WithoutTheMenu_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => rig.Manager.ConnectLeftBT());
            Assert.IsTrue(rig.Manager.IsConnecting(HaptGloveHandler.HandType.Left));
        }

        [Test]
        public void AFailedConnect_FreesTheHandToTryAgain()
        {
            rig.Manager.ConnectLeftBT();
            Private.Call(rig.Manager, "HaptGlove_OnConnectedFailed", HaptGloveHandler.HandType.Left);
            Assert.IsFalse(rig.Manager.IsConnecting(HaptGloveHandler.HandType.Left));

            Assert.DoesNotThrow(() => rig.Manager.ConnectLeftBT());
            Assert.IsTrue(rig.Manager.IsConnecting(HaptGloveHandler.HandType.Left));
        }

        [Test]
        public void BatteryStatus_WithoutTheMenu_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => Private.Call(rig.Manager, "BatteryState", "Left"));
            Assert.DoesNotThrow(() => Private.Call(rig.Manager, "BatteryState", "Right"));
        }

        [Test]
        public void Connect_OnAConnectedGlove_LeavesItConnected()
        {
            rig.Left.bleConnected = true;

            Assert.DoesNotThrow(() => rig.Manager.Connect(HaptGloveHandler.HandType.Left));

            Assert.IsTrue(rig.Manager.IsConnected(HaptGloveHandler.HandType.Left));
            Assert.IsFalse(rig.Manager.IsConnecting(HaptGloveHandler.HandType.Left));
        }

        [Test]
        public void Connect_TwiceInARow_StartsOneConnect()
        {
            rig.Manager.Connect(HaptGloveHandler.HandType.Right);
            Assert.IsTrue(rig.Manager.IsConnecting(HaptGloveHandler.HandType.Right));

            // A second call while the first is under way must not reach the handler, whose
            // connect call would cancel the attempt in progress.
            Assert.DoesNotThrow(() => rig.Manager.Connect(HaptGloveHandler.HandType.Right));
            Assert.IsTrue(rig.Manager.IsConnecting(HaptGloveHandler.HandType.Right));
        }
    }
}
