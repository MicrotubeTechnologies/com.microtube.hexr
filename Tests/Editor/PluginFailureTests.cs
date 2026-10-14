using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ArduinoBluetoothAPILocal;
using HaptGlove;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace HexR.Tests
{
    /// <summary>
    /// A glove connect has to survive the Bluetooth plugin throwing, and tell a plugin that has
    /// gone quiet from an attempt that is simply over.
    ///
    /// On a Quest 3 the second connect of a session left the glove on "Searching..." until the
    /// app restarted: the plugin's name lookup threw NullPointerException on a nameless device
    /// in its cache (see PluginDeviceCache), out of setDeviceName, before the attempt watchdog
    /// was armed, and nothing freed the connect slot. A fake helper throws the same way here.
    /// </summary>
    public class PluginFailureTests
    {
        private TestRig rig;
        private FakeHelper helper;

        [SetUp]
        public void SetUp()
        {
            rig = new TestRig();
            helper = new FakeHelper();
            Assume.That(rig.Left.BuildPlatform, Is.EqualTo(HaptGloveHandler.TargetPlatform.Android));
            Private.Set(rig.Left, "btHelper", helper);
        }

        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
        }

        [Test]
        public void ANameLookupThatThrows_StillScansForTheGlove()
        {
            helper.LookupThrows = true;

            Assert.DoesNotThrow(() => Private.Call(rig.Left, "AndroidConnection"));

            Assert.AreEqual(rig.Left.targetDeviceName, helper.NameSet);
            Assert.AreEqual(1, helper.Scans, "the attempt should go on to scan for the glove");
        }

        [Test]
        public void ANameLookupThatThrowsAfterTheScan_StillTriesToConnect()
        {
            helper.LookupThrows = true;

            Assert.DoesNotThrow(() => Private.Call(rig.Left, "OnScanEnded", helper, new LinkedList<BluetoothDevice>()));

            Assert.AreEqual(1, helper.Connects, "an unresolved glove still gets a connect, whose failure is retried");
        }

        [Test]
        public void AConnectThatThrows_FreesTheHandToTryAgain()
        {
            rig.Manager.ConnectLeftBT();
            Assume.That(rig.Manager.IsConnecting(HaptGloveHandler.HandType.Left));

            // Edit mode runs the manager's coroutine only to its first yield, so step a copy of
            // it past that to where it calls the handler. The handler's disconnect branch is the
            // way left to make that call throw now that the lookups are guarded.
            rig.Left.bleConnected = true;
            helper.DisconnectThrows = true;
            IEnumerator connect = (IEnumerator)Private.Call(rig.Manager, "ConnectWhenPermitted", HaptGloveHandler.HandType.Left);
            LogAssert.Expect(LogType.Error, new Regex("Connecting the Left glove failed"));

            Assert.DoesNotThrow(() => { while (connect.MoveNext()) { } });

            Assert.IsFalse(rig.Manager.IsConnecting(HaptGloveHandler.HandType.Left));
        }

        [Test]
        public void TheAttemptWatchdog_StaysQuietAboutAnAttemptThatConnected()
        {
            // Connected and then disconnected on purpose inside the timeout: the attempt is
            // over, so the watchdog that started with it must not call it "timed out".
            IEnumerator watchdog = StartWatchdog();
            rig.Left.bleConnected = false;
            Private.Set(rig.Left, "connectRequested", false);
            rig.Left.btText = "HaptGloveAR Left Disconnected!";

            while (watchdog.MoveNext()) { }

            Assert.AreEqual("HaptGloveAR Left Disconnected!", rig.Left.btText);
        }

        [Test]
        public void TheAttemptWatchdog_StillReportsAnAttemptThatWentQuiet()
        {
            IEnumerator watchdog = StartWatchdog();
            rig.Left.bleConnected = false;
            Private.Set(rig.Left, "connectRequested", true);

            while (watchdog.MoveNext()) { }

            StringAssert.StartsWith("Connection attempt timed out.", rig.Left.btText);
        }

        // The watchdog for the current attempt, stepped by hand: edit mode would only run it to
        // its WaitForSeconds.
        private IEnumerator StartWatchdog()
        {
            int generation = Private.Get<int>(rig.Left, "connectGeneration");
            return (IEnumerator)Private.Call(rig.Left, "AttemptWatchdog", generation);
        }

        private sealed class FakeHelper : BluetoothHelper
        {
            public bool LookupThrows, DisconnectThrows;
            public string NameSet;
            public int Scans, Connects;

            // What the Java helper threw on the Quest. It stores the target before searching.
            private Exception LookupFailure()
            {
                return new InvalidOperationException("java.lang.NullPointerException: Attempt to invoke virtual method "
                    + "'boolean java.lang.String.equals(java.lang.Object)' on a null object reference");
            }

            public override void setDeviceName(string deviceName)
            {
                NameSet = deviceName;
                if (LookupThrows) throw LookupFailure();
            }

            public override void setDeviceAddress(string deviceAddress)
            {
                if (LookupThrows) throw LookupFailure();
            }

            public override bool isDevicePaired()
            {
                if (LookupThrows) throw LookupFailure();
                return false;
            }

            public override bool ScanNearbyDevices() { Scans++; return true; }
            public override void Connect() { Connects++; }

            public override void Disconnect()
            {
                if (DisconnectThrows) throw new InvalidOperationException("disconnect failed");
            }

            public override bool isConnected() { return false; }
            public override void setLengthBasedStream() { }
            public override void setFixedLengthBasedStream(int length) { }
            public override void setTerminatorBasedStream(string str, bool appendTerminator) { }
            public override void setTerminatorBasedStream(byte[] str, bool appendTerminator) { }
            public override void setCustomStreamManager(StreamManager streamManager) { }
            public override int getId() { return 0; }
            public override string getDeviceName() { return NameSet; }
            public override string getDeviceAddress() { return null; }
            public override BluetoothDevice getBluetoothDevice() { return null; }
            public override LinkedList<BluetoothDevice> getPairedDevicesList() { return new LinkedList<BluetoothDevice>(); }
            public override void StartListening() { }
            public override void SendData(byte[] data) { }
            public override void Subscribe(BluetoothHelperService service) { }
            public override void Subscribe(BluetoothHelperCharacteristic characteristic) { }
            public override void setTxCharacteristic(BluetoothHelperCharacteristic c) { }
            public override void setRxCharacteristic(BluetoothHelperCharacteristic c) { }
            public override void WriteCharacteristic(BluetoothHelperCharacteristic characteristic, byte[] value) { }
            public override void ReadCharacteristic(BluetoothHelperCharacteristic characterstic) { }
            public override List<BluetoothHelperService> getGattServices() { return new List<BluetoothHelperService>(); }
        }
    }
}
