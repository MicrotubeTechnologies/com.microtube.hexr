using System;
using System.IO;
using System.Text.RegularExpressions;
using HaptGlove;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace HexR.Tests
{
    /// <summary>
    /// HexR Remote's side of the phone conversation: reading requests, running them against the
    /// rig, and the status it reports back. No sockets: RemoteCommands and the protocol helpers are
    /// what the network threads hand their work to, and they are tested here directly.
    ///
    /// The reference lines are the contract with the phone. The HexR Tester's
    /// RemoteProtocolTest.kt carries the same strings; change one, change both.
    /// </summary>
    public class RemoteTests
    {
        // What the HexR Tester sends, byte for byte (RemoteProtocolTest.kt builds these).
        private const string ConnectLeftLine = "{\"type\":\"glove\",\"action\":\"connect\",\"hand\":\"left\"}";
        private const string DisconnectRightLine = "{\"type\":\"glove\",\"action\":\"disconnect\",\"hand\":\"right\"}";
        private const string ReleaseLine = "{\"type\":\"release\"}";
        private const string SceneLine = "{\"type\":\"scene\",\"index\":2}";
        private const string CommandLine = "{\"type\":\"command\",\"id\":\"reset\"}";
        private const string StreamStartLine = "{\"type\":\"stream\",\"action\":\"start\"}";
        private const string HelloLine = "{\"type\":\"hello\",\"protocol\":1,\"client\":\"HexR Tester\"}";
        private const string SearchLine = "{\"type\":\"glove\",\"action\":\"search\"}";
        private const string ConnectAddressLine = "{\"type\":\"glove\",\"action\":\"connect\",\"hand\":\"left\",\"address\":\"D4:3D:39:A1:B2:C3\"}";
        private const string ForgetLeftLine = "{\"type\":\"glove\",\"action\":\"forget\",\"hand\":\"left\"}";

        // What the headset sends for ReferenceStatus() below (RemoteProtocolTest.kt parses it).
        private const string ReferenceStatusLine =
            "{\"type\":\"status\",\"protocol\":1,\"id\":\"q3-1\",\"name\":\"Quest 3\",\"model\":\"Oculus Quest 3\"," +
            "\"platform\":\"quest\",\"app\":\"HexR Demo\",\"appVersion\":\"1.2\",\"hexrVersion\":\"0.10.0\"," +
            "\"paused\":false,\"headsetBatteryPct\":71," +
            "\"scene\":{\"index\":1,\"name\":\"Hospital Tutorial\"}," +
            "\"scenes\":[{\"index\":0,\"name\":\"Basic Tutorial\"},{\"index\":1,\"name\":\"Hospital Tutorial\"}]," +
            "\"gloves\":[{\"hand\":\"left\",\"state\":\"connected\",\"message\":\"Connected\",\"batteryPct\":83,\"pressureDeciKPa\":[10,0,0,0,0,0,452],\"savedAddress\":\"D4:3D:39:A1:B2:C3\"}," +
            "{\"hand\":\"right\",\"state\":\"disconnected\",\"message\":\"\",\"batteryPct\":-1,\"pressureDeciKPa\":[],\"savedAddress\":\"\"}]," +
            "\"stream\":{\"state\":\"live\",\"port\":47802,\"width\":1024,\"height\":1024,\"viewers\":1,\"message\":\"\"," +
            "\"audio\":{\"state\":\"live\",\"port\":47803,\"sampleRate\":48000,\"channels\":2,\"message\":\"\"}}," +
            "\"commands\":[{\"id\":\"reset\",\"label\":\"Reset demo\"}]," +
            "\"allowGloveControl\":true,\"allowVideo\":true,\"exposeScenes\":true,\"controllers\":2," +
            "\"haptics\":true,\"clockUs\":1791966541289123," +
            "\"gloveSearch\":{\"state\":\"done\",\"message\":\"\",\"found\":[{\"address\":\"D4:3D:39:D4:E5:F6\",\"name\":\"HaptGloveAR Right\",\"hand\":\"right\",\"rssi\":-48}]}}";

        // A haptics line: the right index fingertip starting to press at 80% (HexR Companion parses it).
        private const string ReferenceHapticsLine =
            "{\"type\":\"haptics\",\"t\":1791966541289123,\"hand\":\"right\",\"channels\":[" +
            "{\"finger\":1,\"mode\":\"pressure\",\"intensityPct\":80,\"frequencyDeciHz\":0,\"tap\":false}]}";

        private TestRig rig;
        private FakeStream stream;
        private RemoteCommands commands;
        private int loadedScene;

        private sealed class FakeStream : IRemoteStream
        {
            public int Starts, Stops;

            public void Start() => Starts++;

            public void Stop() => Stops++;

            public RemoteStreamStatus Read() => new RemoteStreamStatus();
        }

        [SetUp]
        public void SetUp()
        {
            rig = new TestRig();
            stream = new FakeStream();
            loadedScene = -1;
            commands = new RemoteCommands(() => rig.Manager, stream, () => 3, index => loadedScene = index);
        }

        [TearDown]
        public void TearDown()
        {
            rig.Left.ForgetDevice();
            rig.Right.ForgetDevice();
            HexRRemote.UnregisterCommand("reset");
            HexRRemote.UnregisterCommand("boom");
            rig.Dispose();
        }

        private string Run(string line)
        {
            Assert.IsTrue(RemoteProtocol.TryParse(line, out RemoteRequest request, out string error), error);
            return commands.Handle(request);
        }

        // ---- reading requests ------------------------------------------------------------------

        [Test]
        public void Parse_ReadsEveryReferenceRequest()
        {
            Assert.IsTrue(RemoteProtocol.TryParse(ConnectLeftLine, out RemoteRequest connect, out _));
            Assert.AreEqual(("glove", "connect", "left"), (connect.type, connect.action, connect.hand));

            Assert.IsTrue(RemoteProtocol.TryParse(DisconnectRightLine, out RemoteRequest disconnect, out _));
            Assert.AreEqual(("disconnect", "right"), (disconnect.action, disconnect.hand));

            Assert.IsTrue(RemoteProtocol.TryParse(SceneLine, out RemoteRequest scene, out _));
            Assert.AreEqual(2, scene.index);

            Assert.IsTrue(RemoteProtocol.TryParse(CommandLine, out RemoteRequest command, out _));
            Assert.AreEqual("reset", command.id);

            Assert.IsTrue(RemoteProtocol.TryParse(HelloLine, out RemoteRequest hello, out _));
            Assert.AreEqual((1, "HexR Tester"), (hello.protocol, hello.client));

            Assert.IsTrue(RemoteProtocol.TryParse(ConnectAddressLine, out RemoteRequest byAddress, out _));
            Assert.AreEqual(("connect", "left", "D4:3D:39:A1:B2:C3"), (byAddress.action, byAddress.hand, byAddress.address));
        }

        [Test]
        public void Parse_AMissingIndex_ReadsAsNotGiven_NotAsSceneZero()
        {
            Assert.IsTrue(RemoteProtocol.TryParse(ReleaseLine, out RemoteRequest request, out _));
            Assert.AreEqual(-1, request.index);
        }

        [Test]
        public void Parse_RejectsWhatIsNotARequest()
        {
            Assert.IsFalse(RemoteProtocol.TryParse("hello there", out _, out string notJson));
            Assert.IsNotEmpty(notJson);
            Assert.IsFalse(RemoteProtocol.TryParse("{\"hand\":\"left\"}", out _, out string noType));
            Assert.IsNotEmpty(noType);
        }

        // ---- gloves ------------------------------------------------------------------------------

        [Test]
        public void ConnectLeft_StartsConnectingTheLeftGlove()
        {
            Assert.IsNull(Run(ConnectLeftLine));
            Assert.IsTrue(rig.Manager.IsConnecting(HaptGloveHandler.HandType.Left));
            Assert.IsFalse(rig.Manager.IsConnecting(HaptGloveHandler.HandType.Right));
        }

        [Test]
        public void Connect_WhenGloveControlIsOff_IsRefused()
        {
            commands.AllowGloveControl = false;
            Assert.IsNotNull(Run(ConnectLeftLine));
            Assert.IsFalse(rig.Manager.IsConnecting(HaptGloveHandler.HandType.Left));
        }

        [Test]
        public void Connect_ForAHandThatIsNeither_IsRefused()
        {
            Assert.IsNotNull(Run("{\"type\":\"glove\",\"action\":\"connect\",\"hand\":\"middle\"}"));
        }

        [Test]
        public void Disconnect_WhileStillConnecting_SaysSo_RatherThanDoingNothingSilently()
        {
            Run("{\"type\":\"glove\",\"action\":\"connect\",\"hand\":\"right\"}");
            StringAssert.Contains("still connecting", Run(DisconnectRightLine));
        }

        [Test]
        public void Disconnect_AGloveThatIsNotConnected_IsHarmless()
        {
            Assert.IsNull(Run(DisconnectRightLine));
        }

        [Test]
        public void Release_SwitchesEveryChannelOffOnConnectedGloves()
        {
            rig.Left.bleConnected = true;
            rig.Left.haptics.HEXRPressure(Haptics.Finger.Index, true, 0.5f, 1f);

            Assert.IsNull(Run(ReleaseLine));
            Assert.AreEqual(Haptics.HapticMode.Off, rig.Left.haptics.GetChannelState(Haptics.Finger.Index).Mode);
        }

        // ---- scenes, commands, stream ------------------------------------------------------------

        [Test]
        public void Scene_IsRefusedUnlessTheAppExposesScenes()
        {
            Assert.IsNotNull(Run(SceneLine));
            Assert.AreEqual(-1, loadedScene);
        }

        [Test]
        public void Scene_LoadsAnExistingBuildScene_AndRefusesOneThatIsNot()
        {
            commands.ExposeScenes = true;
            Assert.IsNull(Run(SceneLine));
            Assert.AreEqual(2, loadedScene);

            loadedScene = -1;
            Assert.IsNotNull(Run("{\"type\":\"scene\",\"index\":3}"));
            Assert.IsNotNull(Run("{\"type\":\"scene\"}"));
            Assert.AreEqual(-1, loadedScene);
        }

        [Test]
        public void Command_RunsWhatTheAppRegistered_AndListsIt()
        {
            int runs = 0;
            HexRRemote.RegisterCommand("reset", "Reset demo", () => runs++);

            Assert.IsNull(Run(CommandLine));
            Assert.AreEqual(1, runs);

            RemoteCommandInfo[] listed = HexRRemote.CommandList();
            Assert.AreEqual(1, listed.Length);
            Assert.AreEqual(("reset", "Reset demo"), (listed[0].id, listed[0].label));
        }

        [Test]
        public void Command_ThatIsNotRegistered_IsRefused()
        {
            StringAssert.Contains("no command", Run(CommandLine));
        }

        [Test]
        public void Command_ThatThrows_ReportsTheFailure_InsteadOfTakingTheRemoteDown()
        {
            HexRRemote.RegisterCommand("boom", "Boom", () => throw new InvalidOperationException("kaput"));
            LogAssert.Expect(LogType.Exception, new Regex("kaput"));
            StringAssert.Contains("kaput", Run("{\"type\":\"command\",\"id\":\"boom\"}"));
        }

        [Test]
        public void Stream_StartsAndStops_OnlyWhenTheAppSharesItsView()
        {
            Assert.IsNull(Run(StreamStartLine));
            Assert.IsNull(Run("{\"type\":\"stream\",\"action\":\"stop\"}"));
            Assert.AreEqual((1, 1), (stream.Starts, stream.Stops));

            commands.AllowVideo = false;
            Assert.IsNotNull(Run(StreamStartLine));
            Assert.AreEqual(1, stream.Starts);
        }

        [Test]
        public void AnUnknownRequest_IsRefusedByName()
        {
            StringAssert.Contains("teleport", Run("{\"type\":\"teleport\"}"));
        }

        // ---- picking one glove out of several ----------------------------------------------------

        [Test]
        public void ConnectWithAnAddress_SavesThatGlove_AndStartsConnecting()
        {
            Assert.IsNull(Run(ConnectAddressLine));
            Assert.AreEqual("D4:3D:39:A1:B2:C3", rig.Manager.SavedGloveAddress(HaptGloveHandler.HandType.Left));
            Assert.IsTrue(rig.Manager.IsConnecting(HaptGloveHandler.HandType.Left));
            Assert.AreEqual("", rig.Manager.SavedGloveAddress(HaptGloveHandler.HandType.Right));
        }

        [Test]
        public void Forget_ClearsTheSavedGlove()
        {
            rig.Left.RememberDevice("D4:3D:39:A1:B2:C3");
            Assert.IsNull(Run(ForgetLeftLine));
            Assert.AreEqual("", rig.Manager.SavedGloveAddress(HaptGloveHandler.HandType.Left));
        }

        [Test]
        public void Search_WithBothGlovesConnected_IsRefused_WithWhatToDo()
        {
            rig.Left.bleConnected = true;
            rig.Right.bleConnected = true;
            StringAssert.Contains("Disconnect one", Run(SearchLine));
            Assert.AreEqual(HexRManager.NearbySearchState.Idle, rig.Manager.NearbySearch);
        }

        [Test]
        public void Search_WithoutTheHeadsetsBluetooth_FailsInTheStatus_WithTheReason()
        {
            // No Bluetooth helper in edit mode, as off the headset.
            string error = Run(SearchLine);
            Assert.IsNotNull(error);
            RemoteGloveSearch search = RemoteCommands.GloveSearchStatus(rig.Manager);
            Assert.AreEqual((RemoteProtocol.SearchFailed, error), (search.state, search.message));
        }

        [Test]
        public void HandFromName_ReadsTheAdvertisedName()
        {
            Assert.AreEqual(HaptGloveHandler.HandType.Left, HexRManager.HandFromName("HaptGloveAR Left"));
            Assert.AreEqual(HaptGloveHandler.HandType.Right, HexRManager.HandFromName("HexR 03 Right"));
            Assert.IsNull(HexRManager.HandFromName("HexR 03"));
            Assert.IsNull(HexRManager.HandFromName(null));
        }

        // ---- status ------------------------------------------------------------------------------

        [Test]
        public void GloveStatus_ReportsState_Battery_AndGaugePressure()
        {
            rig.Left.bleConnected = true;
            rig.Left.btText = "Connected";
            rig.Left.haptics.batteryLevel = 0.83f;
            // Absolute pascals, the way the HexR Tools window reads them: 101 kPa is 1 kPa gauge.
            rig.Left.haptics.pressureData = new[] { 101000, 100000, 100000, 100000, 100000, 100000, 145200 };

            RemoteGloveStatus[] gloves = RemoteCommands.GloveStatus(rig.Manager);

            Assert.AreEqual(("left", RemoteProtocol.Connected, 83), (gloves[0].hand, gloves[0].state, gloves[0].batteryPct));
            Assert.AreEqual("Connected", gloves[0].message);
            CollectionAssert.AreEqual(new[] { 10, 0, 0, 0, 0, 0, 452 }, gloves[0].pressureDeciKPa);
            Assert.AreEqual(("right", RemoteProtocol.Disconnected, -1), (gloves[1].hand, gloves[1].state, gloves[1].batteryPct));
            Assert.IsEmpty(gloves[1].pressureDeciKPa);
            Assert.AreEqual(("", ""), (gloves[0].savedAddress, gloves[1].savedAddress));

            rig.Left.RememberDevice("D4:3D:39:A1:B2:C3");
            Assert.AreEqual("D4:3D:39:A1:B2:C3", RemoteCommands.GloveStatus(rig.Manager)[0].savedAddress);
        }

        [Test]
        public void GloveStatus_WithNoRig_StillListsBothHands_AsMissing()
        {
            RemoteGloveStatus[] gloves = RemoteCommands.GloveStatus(null);
            Assert.AreEqual(2, gloves.Length);
            Assert.AreEqual((RemoteProtocol.Missing, RemoteProtocol.Missing), (gloves[0].state, gloves[1].state));
        }

        [Test]
        public void ToDeciKPa_ReadsAbsolutePascalsAndGaugeKPaTheSameWay_AsTheHexRTester()
        {
            Assert.AreEqual(452, RemoteProtocol.ToDeciKPa(145200f));
            Assert.AreEqual(452, RemoteProtocol.ToDeciKPa(45.2f));
            Assert.AreEqual(0, RemoteProtocol.ToDeciKPa(float.NaN));
        }

        [Test]
        public void Status_MatchesTheReferenceLine_ThePhoneIsTestedAgainst()
        {
            Assert.AreEqual(ReferenceStatusLine, RemoteProtocol.ToLine(ReferenceStatus()));
        }

        [Test]
        public void HexRVersion_MatchesPackageJson()
        {
            string json = File.ReadAllText("Packages/com.microtube.hexr/package.json");
            Match version = Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
            Assert.IsTrue(version.Success);
            Assert.AreEqual(version.Groups[1].Value, HexRRemote.HexRVersion);
        }

        private static RemoteStatus ReferenceStatus()
        {
            return new RemoteStatus
            {
                id = "q3-1",
                name = "Quest 3",
                model = "Oculus Quest 3",
                platform = "quest",
                app = "HexR Demo",
                appVersion = "1.2",
                hexrVersion = "0.10.0",
                headsetBatteryPct = 71,
                scene = new RemoteSceneInfo { index = 1, name = "Hospital Tutorial" },
                scenes = new[]
                {
                    new RemoteSceneInfo { index = 0, name = "Basic Tutorial" },
                    new RemoteSceneInfo { index = 1, name = "Hospital Tutorial" },
                },
                gloves = new[]
                {
                    new RemoteGloveStatus
                    {
                        hand = "left", state = "connected", message = "Connected", batteryPct = 83,
                        pressureDeciKPa = new[] { 10, 0, 0, 0, 0, 0, 452 },
                        savedAddress = "D4:3D:39:A1:B2:C3",
                    },
                    new RemoteGloveStatus { hand = "right", state = "disconnected" },
                },
                stream = new RemoteStreamStatus
                {
                    state = "live", port = 47802, width = 1024, height = 1024, viewers = 1,
                    audio = new RemoteAudioStatus { state = "live", port = 47803, sampleRate = 48000, channels = 2 },
                },
                commands = new[] { new RemoteCommandInfo { id = "reset", label = "Reset demo" } },
                allowGloveControl = true,
                allowVideo = true,
                exposeScenes = true,
                controllers = 2,
                haptics = true,
                clockUs = 1791966541289123,
                gloveSearch = new RemoteGloveSearch
                {
                    state = "done",
                    found = new[]
                    {
                        new RemoteNearbyGlove { address = "D4:3D:39:D4:E5:F6", name = "HaptGloveAR Right", hand = "right", rssi = -48 },
                    },
                },
            };
        }

        // ---- haptics -----------------------------------------------------------------------------

        [Test]
        public void Haptics_MatchesTheReferenceLine_ThePhoneIsTestedAgainst()
        {
            RemoteHapticsMessage message = new RemoteHapticsMessage
            {
                t = 1791966541289123,
                hand = "right",
                channels = new[] { new RemoteHapticChannel { finger = 1, mode = "pressure", intensityPct = 80 } },
            };
            Assert.AreEqual(ReferenceHapticsLine, RemoteProtocol.ToLine(message));
        }

        [Test]
        public void HapticsFeed_SendsAPress_ThenItsRelease_StampedWithTheClock()
        {
            long now = 1000;
            RemoteHaptics feed = new RemoteHaptics(() => now);
            var lines = new System.Collections.Generic.List<string>();
            feed.Poll(rig.Manager, lines);
            Assert.IsEmpty(lines, "nothing is on yet, so nothing is sent");

            rig.Right.haptics.HEXRPressure(Haptics.Finger.Index, true, 0.8f, 1f);
            now = 2000;
            feed.Poll(rig.Manager, lines);
            Assert.AreEqual(1, lines.Count);
            RemoteHapticsMessage on = JsonUtility.FromJson<RemoteHapticsMessage>(lines[0]);
            Assert.AreEqual((2000L, "right", 1), (on.t, on.hand, on.channels.Length));
            Assert.AreEqual((1, "pressure", 80, false), (on.channels[0].finger, on.channels[0].mode, on.channels[0].intensityPct, on.channels[0].tap));

            lines.Clear();
            feed.Poll(rig.Manager, lines);
            Assert.IsEmpty(lines, "held, so nothing new");

            rig.Right.haptics.HEXRPressure(Haptics.Finger.Index, false, 0.8f, 1f);
            feed.Poll(rig.Manager, lines);
            RemoteHapticsMessage off = JsonUtility.FromJson<RemoteHapticsMessage>(lines[0]);
            Assert.AreEqual((1, "off"), (off.channels[0].finger, off.channels[0].mode));
        }

        [Test]
        public void HapticsFeed_OnAndOffWithinOneFrame_IsSentAsATap()
        {
            RemoteHaptics feed = new RemoteHaptics(() => 1);
            var lines = new System.Collections.Generic.List<string>();
            feed.Poll(rig.Manager, lines);

            rig.Left.haptics.HEXRVibration(Haptics.Finger.Palm, true, 6f, 0.5f);
            rig.Left.haptics.HEXRVibration(Haptics.Finger.Palm, false, 6f, 0.5f);
            feed.Poll(rig.Manager, lines);

            RemoteHapticChannel tap = JsonUtility.FromJson<RemoteHapticsMessage>(lines[0]).channels[0];
            Assert.AreEqual((5, "vibration", 50, 60, true), (tap.finger, tap.mode, tap.intensityPct, tap.frequencyDeciHz, tap.tap));
        }

        [Test]
        public void HapticsFeed_Snapshot_HasEveryChannelOfBothHands()
        {
            RemoteHaptics feed = new RemoteHaptics(() => 1);
            rig.Left.haptics.HEXRPressure(Haptics.Finger.Thumb, true, 1f, 1f);
            feed.Poll(rig.Manager, new System.Collections.Generic.List<string>());

            string[] snapshot = feed.Snapshot();
            Assert.AreEqual(2, snapshot.Length);
            RemoteHapticsMessage left = JsonUtility.FromJson<RemoteHapticsMessage>(snapshot[0]);
            Assert.AreEqual(("left", 6), (left.hand, left.channels.Length));
            Assert.AreEqual(("pressure", 100), (left.channels[0].mode, left.channels[0].intensityPct));
            Assert.AreEqual("off", left.channels[1].mode);
        }

        [Test]
        public void HapticsFeed_ARigGoingAway_TurnsItsChannelsOff()
        {
            RemoteHaptics feed = new RemoteHaptics(() => 1);
            var lines = new System.Collections.Generic.List<string>();
            rig.Right.haptics.HEXRPressure(Haptics.Finger.Ring, true, 0.5f, 1f);
            feed.Poll(rig.Manager, lines);

            lines.Clear();
            feed.Poll(null, lines);
            RemoteHapticsMessage gone = JsonUtility.FromJson<RemoteHapticsMessage>(lines[0]);
            Assert.AreEqual(("right", 3, "off"), (gone.hand, gone.channels[0].finger, gone.channels[0].mode));
        }
    }
}
