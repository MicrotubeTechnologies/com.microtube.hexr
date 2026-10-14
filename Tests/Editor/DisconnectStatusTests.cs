using HaptGlove;
using NUnit.Framework;
using TMPro;

namespace HexR.Tests
{
    /// <summary>
    /// What the menu says when a glove disconnects. One event reports both a drop, which the
    /// handler is already reconnecting, and a disconnect someone asked for, which nothing will
    /// undo. On a Quest 3 the second read "disconnected — reconnecting…", so the wearer waited
    /// for a reconnect that never came.
    /// </summary>
    public class DisconnectStatusTests
    {
        private TestRig rig;
        private TextMeshProUGUI status;

        [SetUp]
        public void SetUp()
        {
            rig = new TestRig();
            status = rig.Make("Right status").AddComponent<TextMeshProUGUI>();
            rig.Manager.RightBtText = status;
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
        }

        [Test]
        public void ADisconnectThatWasAskedFor_DoesNotPromiseAReconnect()
        {
            Private.Set(rig.Right, "connectRequested", false);

            Private.Call(rig.Manager, "HaptGlove_OnDisconnected", HaptGloveHandler.HandType.Right);

            Assert.AreEqual("Right disconnected", status.text);
        }

        [Test]
        public void ADrop_SaysTheGloveIsReconnecting()
        {
            // As the handler leaves it on a drop: a fresh connect campaign, opened before the
            // event is raised.
            Private.Set(rig.Right, "connectRequested", true);

            Private.Call(rig.Manager, "HaptGlove_OnDisconnected", HaptGloveHandler.HandType.Right);

            Assert.AreEqual("Right disconnected — reconnecting…", status.text);
        }
    }
}
