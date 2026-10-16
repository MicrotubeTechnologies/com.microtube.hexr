using System;
using HaptGlove;
using UnityEngine;

namespace HexR
{
    /// <summary>
    /// HexR Remote's wire protocol, version 1. Documentation~/RemoteProtocol.md is the contract.
    ///
    /// The phone side (the HexR Tester's RemoteProtocol.kt) implements the same thing in another
    /// language, so the reference lines in Tests/Editor/RemoteProtocolTests.cs are repeated in its
    /// RemoteProtocolTest.kt. Change the wire format in both places, or one suite goes red.
    ///
    /// Everything is JsonUtility-shaped on purpose: the package takes no JSON dependency. That is
    /// why states are strings rather than enums (JsonUtility writes enums as numbers), numbers are
    /// integers where they can be (no float noise on the wire), and requests are read into an
    /// object whose defaults mean "not given" (index -1), since a missing field would otherwise
    /// read as 0.
    /// </summary>
    internal static class RemoteProtocol
    {
        public const int Version = 1;

        /// <summary>A control line longer than this is not a HexR request; the connection is dropped.</summary>
        public const int MaxLineBytes = 16 * 1024;

        public const int DefaultDiscoveryPort = 47800;
        public const int DefaultControlPort = 47801;
        public const int DefaultVideoPort = 47802;

        // Request and message types.
        public const string Hello = "hello";
        public const string Ping = "ping";
        public const string Glove = "glove";
        public const string Release = "release";
        public const string Scene = "scene";
        public const string Command = "command";
        public const string Stream = "stream";
        public const string Status = "status";
        public const string Error = "error";
        public const string Announce = "announce";
        public const string HapticsMessage = "haptics";

        // What a haptic channel is doing.
        public const string HapticOff = "off";
        public const string HapticPressure = "pressure";
        public const string HapticVibration = "vibration";
        public const string HapticPulse = "pulse";

        public const string Left = "left";
        public const string Right = "right";

        // Glove states.
        public const string Connected = "connected";
        public const string Connecting = "connecting";
        public const string Disconnected = "disconnected";
        public const string Missing = "missing";

        /// <summary>
        /// Reads one control line. False, with a reason a person can act on, if it is not a request.
        /// </summary>
        public static bool TryParse(string line, out RemoteRequest request, out string error)
        {
            request = null;
            error = null;
            if (string.IsNullOrWhiteSpace(line))
            {
                error = "Empty request.";
                return false;
            }

            RemoteRequest parsed = new RemoteRequest();
            try
            {
                JsonUtility.FromJsonOverwrite(line, parsed);
            }
            catch (Exception)
            {
                error = "That request is not JSON.";
                return false;
            }

            if (string.IsNullOrEmpty(parsed.type))
            {
                error = "The request has no type.";
                return false;
            }

            request = parsed;
            return true;
        }

        public static bool TryParseHand(string hand, out HaptGloveHandler.HandType type)
        {
            if (string.Equals(hand, Left, StringComparison.OrdinalIgnoreCase))
            {
                type = HaptGloveHandler.HandType.Left;
                return true;
            }

            if (string.Equals(hand, Right, StringComparison.OrdinalIgnoreCase))
            {
                type = HaptGloveHandler.HandType.Right;
                return true;
            }

            type = HaptGloveHandler.HandType.Left;
            return false;
        }

        public static string HandName(HaptGloveHandler.HandType hand)
        {
            return hand == HaptGloveHandler.HandType.Left ? Left : Right;
        }

        /// <summary>
        /// One glove pressure reading as tenths of a kPa above ambient.
        ///
        /// What the glove reports is not settled: the firmware comments it as kPa gauge, the
        /// HexR Tools window converts it as absolute pascals. The HexR Tester resolves it by
        /// looking (Protocol.rawToKpa), and so does this, with the same threshold, so a glove reads
        /// the same on the phone's Test tab and through a headset. At rest a gauge reading is near 0
        /// and an absolute one near 100000.
        /// </summary>
        public static int ToDeciKPa(float raw)
        {
            float kpa = raw > 10000f ? (raw - 100000f) / 1000f : raw;
            if (float.IsNaN(kpa) || float.IsInfinity(kpa))
            {
                return 0;
            }

            return Mathf.RoundToInt(kpa * 10f);
        }

        public static string ToLine(object message)
        {
            return JsonUtility.ToJson(message);
        }

        public static string ErrorLine(string requestType, string message)
        {
            return ToLine(new RemoteError { request = requestType ?? "", message = message ?? "" });
        }
    }

    // The wire classes below are filled by JsonUtility, or on Android by the Java side, so the
    // compiler cannot see their fields being assigned.
#pragma warning disable CS0649
    /// <summary>Anything a phone sends. Unused fields stay at their "not given" defaults.</summary>
    [Serializable]
    internal class RemoteRequest
    {
        public string type = "";
        public string action = "";
        public string hand = "";
        public int index = -1;
        public string id = "";
        public int protocol;
        public string client = "";
    }

    /// <summary>Broadcast on UDP every two seconds so phones find the headset with no typing.</summary>
    [Serializable]
    internal class RemoteAnnounce
    {
        public int hexr = RemoteProtocol.Version;
        public string type = RemoteProtocol.Announce;
        public string id = "";
        public string name = "";
        public string model = "";
        public string platform = "";
        public string app = "";
        public string appId = "";
        public string hexrVersion = "";
        public int control;
        public int video;
        public long startedAt;
    }

    /// <summary>The headset's answer to "hello".</summary>
    [Serializable]
    internal class RemoteWelcome
    {
        public string type = RemoteProtocol.Hello;
        public int protocol = RemoteProtocol.Version;
        public string id = "";
        public int control;
        public int video;
    }

    [Serializable]
    internal class RemoteError
    {
        public string type = RemoteProtocol.Error;
        public string request = "";
        public string message = "";
    }

    [Serializable]
    internal class RemoteStatus
    {
        public string type = RemoteProtocol.Status;
        public int protocol = RemoteProtocol.Version;
        public string id = "";
        public string name = "";
        public string model = "";
        public string platform = "";
        public string app = "";
        public string appVersion = "";
        public string hexrVersion = "";
        public bool paused;
        public int headsetBatteryPct = -1;
        public RemoteSceneInfo scene = new RemoteSceneInfo();
        public RemoteSceneInfo[] scenes = new RemoteSceneInfo[0];
        public RemoteGloveStatus[] gloves = new RemoteGloveStatus[0];
        public RemoteStreamStatus stream = new RemoteStreamStatus();
        public RemoteCommandInfo[] commands = new RemoteCommandInfo[0];
        public bool allowGloveControl;
        public bool allowVideo;
        public bool exposeScenes;
        public int controllers;

        /// <summary>This headset sends "haptics" lines. Headsets before it do not send the field.</summary>
        public bool haptics;

        /// <summary>The headset's clock when this status was made, in µs: the clock haptics and video are stamped with.</summary>
        public long clockUs;
    }

    /// <summary>
    /// What one glove's haptic channels started doing, sent the moment it happens: only the
    /// channels that changed, or all six when a phone connects.
    /// </summary>
    [Serializable]
    internal class RemoteHapticsMessage
    {
        public string type = RemoteProtocol.HapticsMessage;

        /// <summary>When, in µs on RemoteClock: the video's presentation-time clock.</summary>
        public long t;

        public string hand = "";
        public RemoteHapticChannel[] channels = new RemoteHapticChannel[0];
    }

    [Serializable]
    internal class RemoteHapticChannel
    {
        /// <summary>0 thumb, 1 index, 2 middle, 3 ring, 4 pinky, 5 palm (Haptics.Finger).</summary>
        public int finger;

        /// <summary>"off", "pressure", "vibration" or "pulse".</summary>
        public string mode = RemoteProtocol.HapticOff;

        /// <summary>The command's intensity, 10-100 while on (0.1-1 in the API).</summary>
        public int intensityPct;

        /// <summary>Vibration and pulse frequency in tenths of a hertz; 0 for pressure.</summary>
        public int frequencyDeciHz;

        /// <summary>Switched on and off again within one frame: show it briefly.</summary>
        public bool tap;
    }

    [Serializable]
    internal class RemoteSceneInfo
    {
        public int index = -1;
        public string name = "";
    }

    [Serializable]
    internal class RemoteGloveStatus
    {
        public string hand = "";
        public string state = RemoteProtocol.Missing;
        public string message = "";
        public int batteryPct = -1;

        /// <summary>Thumb, index, middle, ring, pinky, palm, then the pump's source tank.</summary>
        public int[] pressureDeciKPa = new int[0];
    }

    [Serializable]
    internal class RemoteStreamStatus
    {
        public string state = RemoteStreamState.Off;
        public int port;
        public int width;
        public int height;
        public int viewers;
        public string message = "";
    }

    [Serializable]
    internal class RemoteCommandInfo
    {
        public string id = "";
        public string label = "";
    }

    internal static class RemoteStreamState
    {
        public const string Off = "off";
        public const string Requesting = "requesting";
        public const string Live = "live";
        public const string Denied = "denied";
        public const string Unsupported = "unsupported";
        public const string Error = "error";
    }
#pragma warning restore CS0649
}
