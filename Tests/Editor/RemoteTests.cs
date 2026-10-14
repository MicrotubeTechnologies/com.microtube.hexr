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

        // What the headset sends for ReferenceStatus() below (RemoteProtocolTest.kt parses it).
        private const string ReferenceStatusLine =
            "{\"type\":\"status\",\"protocol\":1,\"id\":\"q3-1\",\"name\":\"Quest 3\",\"model\":\"Oculus Quest 3\"," +
            "\"platform\":\"quest\",\"app\":\"HexR Demo\",\"appVersion\":\"1.2\",\"hexrVersion\":\"0.10.0\"," +
            "\"paused\":false,\"headsetBatteryPct\":71," +
            "\"scene\":{\"index\":1,\"name\":\"Hospital Tutorial\"}," +
            "\"scenes\":[{\"index\":0,\"name\":\"Basic Tutorial\"},{\"index\":1,\"name\":\"Hospital Tutorial\"}]," +
            "\"gloves\":[{\"hand\":\"left\",\"state\":\"connected\",\"message\":\"Connected\",\"batteryPct\":83,\"pressureDeciKPa\":[10,0,0,0,0,0,452]}," +
            "{\"hand\":\"right\",\"state\":\"disconnected\",\"message\":\"\",\"batteryPct\":-1,\"pressureDeciKPa\":[]}]," +
            "\"stream\":{\"state\":\"live\",\"port\":47802,\"width\":1024,\"height\":1024,\"viewers\":1,\"message\":\"\"}," +
            "\"commands\":[{\"id\":\"reset\",\"label\":\"Reset demo\"}]," +
            "\"allowGloveControl\":true,\"allowVideo\":true,\"exposeScenes\":true,\"controllers\":2}";

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
                    },
                    new RemoteGloveStatus { hand = "right", state = "disconnected" },
                },
                stream = new RemoteStreamStatus { state = "live", port = 47802, width = 1024, height = 1024, viewers = 1 },
                commands = new[] { new RemoteCommandInfo { id = "reset", label = "Reset demo" } },
                allowGloveControl = true,
                allowVideo = true,
                exposeScenes = true,
                controllers = 2,
            };
        }
    }
}
