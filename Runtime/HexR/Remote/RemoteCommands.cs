using System;
using System.Collections.Generic;
using HaptGlove;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HexR
{
    /// <summary>
    /// Turns a phone's request into calls on the existing HexR API, on Unity's main thread.
    ///
    /// Nothing here touches a socket, so the tests drive it directly with a TestRig.
    /// Handle returns null on success, or the reason in words for the phone to show.
    /// </summary>
    internal sealed class RemoteCommands
    {
        private readonly Func<HexRManager> manager;
        private readonly IRemoteStream stream;
        private readonly Func<int> sceneCount;
        private readonly Action<int> loadScene;

        public bool AllowGloveControl = true;
        public bool AllowVideo = true;
        public bool ExposeScenes;

        public RemoteCommands(Func<HexRManager> manager, IRemoteStream stream, Func<int> sceneCount, Action<int> loadScene)
        {
            this.manager = manager;
            this.stream = stream;
            this.sceneCount = sceneCount;
            this.loadScene = loadScene;
        }

        public string Handle(RemoteRequest request)
        {
            switch (request.type)
            {
                case RemoteProtocol.Glove:
                    return Glove(request);
                case RemoteProtocol.Release:
                    return Release(request);
                case RemoteProtocol.Scene:
                    return Scene(request);
                case RemoteProtocol.Command:
                    return Command(request);
                case RemoteProtocol.Stream:
                    return Stream(request);
                default:
                    return "This headset does not know the request \"" + request.type + "\".";
            }
        }

        private string Glove(RemoteRequest request)
        {
            if (!AllowGloveControl)
            {
                return "This app does not let a phone connect its gloves.";
            }

            if (!RemoteProtocol.TryParseHand(request.hand, out HaptGloveHandler.HandType hand))
            {
                return "Say which glove: \"left\" or \"right\".";
            }

            HexRManager rig = manager();
            if (rig == null)
            {
                return "There is no HexR rig in this scene.";
            }

            HaptGloveHandler handler = HandlerFor(rig, hand);
            if (handler == null)
            {
                return "This rig has no " + RemoteProtocol.HandName(hand) + " glove.";
            }

            switch (request.action)
            {
                case "connect":
                    // Does nothing if already connected or connecting, so a double tap is harmless.
                    // It may put a Bluetooth permission prompt up in the headset first; the glove's
                    // status message tells the phone so.
                    rig.Connect(hand);
                    return null;

                case "disconnect":
                    if (!rig.IsConnected(hand))
                    {
                        // HexRManager.Disconnect ignores a glove that is still connecting, and a
                        // silent no-op reads as a broken button.
                        return rig.IsConnecting(hand)
                            ? "The " + RemoteProtocol.HandName(hand) + " glove is still connecting. Try again once it has."
                            : null;
                    }

                    // Never BTDisconnection: that removes the plugin callbacks, and the glove could
                    // not be connected again without restarting the app.
                    rig.Disconnect(hand);
                    return null;

                default:
                    return "A glove request needs \"connect\" or \"disconnect\".";
            }
        }

        private string Release(RemoteRequest request)
        {
            if (!AllowGloveControl)
            {
                return "This app does not let a phone control its gloves.";
            }

            HexRManager rig = manager();
            if (rig == null)
            {
                return "There is no HexR rig in this scene.";
            }

            bool anyHand = string.IsNullOrEmpty(request.hand);
            bool oneHand = RemoteProtocol.TryParseHand(request.hand, out HaptGloveHandler.HandType only);
            if (!anyHand && !oneHand)
            {
                return "Say which glove: \"left\", \"right\", or neither for both.";
            }

            foreach (HaptGloveHandler.HandType hand in Hands)
            {
                if (!anyHand && hand != only)
                {
                    continue;
                }

                HaptGloveHandler handler = HandlerFor(rig, hand);
                if (handler != null && handler.bleConnected)
                {
                    handler.ReleaseAllChannels();
                }
            }

            return null;
        }

        private string Scene(RemoteRequest request)
        {
            if (!ExposeScenes)
            {
                return "This app does not let a phone change its scene.";
            }

            int count = sceneCount();
            if (request.index < 0 || request.index >= count)
            {
                return "This app has no scene " + request.index + ".";
            }

            loadScene(request.index);
            return null;
        }

        private string Command(RemoteRequest request)
        {
            if (!HexRRemote.TryGetCommand(request.id, out Action action))
            {
                return "This app has no command \"" + request.id + "\".";
            }

            // A command registered by an object that has since been destroyed: Unity's == null on
            // the target says so, where the delegate itself would still look alive.
            if (action.Target is UnityEngine.Object target && target == null)
            {
                return "That command belonged to something no longer in the scene.";
            }

            try
            {
                action();
                return null;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return "The command failed in the headset: " + e.Message;
            }
        }

        private string Stream(RemoteRequest request)
        {
            switch (request.action)
            {
                case "start":
                    if (!AllowVideo)
                    {
                        return "This app does not share its view.";
                    }

                    stream.Start();
                    return null;

                case "stop":
                    stream.Stop();
                    return null;

                default:
                    return "A stream request needs \"start\" or \"stop\".";
            }
        }

        // ---- status ----------------------------------------------------------------------------

        private static readonly HaptGloveHandler.HandType[] Hands =
        {
            HaptGloveHandler.HandType.Left,
            HaptGloveHandler.HandType.Right,
        };

        /// <summary>Both gloves, always in left, right order, whether or not the rig has them.</summary>
        public static RemoteGloveStatus[] GloveStatus(HexRManager rig)
        {
            RemoteGloveStatus[] gloves = new RemoteGloveStatus[Hands.Length];
            for (int i = 0; i < Hands.Length; i++)
            {
                HaptGloveHandler.HandType hand = Hands[i];
                RemoteGloveStatus glove = new RemoteGloveStatus { hand = RemoteProtocol.HandName(hand) };
                gloves[i] = glove;

                HaptGloveHandler handler = rig != null ? HandlerFor(rig, hand) : null;
                if (handler == null)
                {
                    continue;
                }

                bool connected = rig.IsConnected(hand);
                glove.state = connected
                    ? RemoteProtocol.Connected
                    : rig.IsConnecting(hand) ? RemoteProtocol.Connecting : RemoteProtocol.Disconnected;
                glove.message = handler.btText ?? "";

                if (!connected)
                {
                    continue;
                }

                // 0 means no battery frame has arrived yet, not an empty glove: a glove at 0%
                // would not be running to say so.
                float battery = handler.GetBatteryLevel();
                glove.batteryPct = battery > 0f ? Mathf.Clamp(Mathf.RoundToInt(battery * 100f), 0, 100) : -1;

                int[] raw = handler.GetAirPressure();
                if (raw != null)
                {
                    int[] pressure = new int[raw.Length];
                    for (int c = 0; c < raw.Length; c++)
                    {
                        pressure[c] = RemoteProtocol.ToDeciKPa(raw[c]);
                    }

                    glove.pressureDeciKPa = pressure;
                }
            }

            return gloves;
        }

        private static HaptGloveHandler HandlerFor(HexRManager rig, HaptGloveHandler.HandType hand)
        {
            return hand == HaptGloveHandler.HandType.Left ? rig.leftHand : rig.rightHand;
        }

        /// <summary>The scene the phone is told about, and the list it may switch between.</summary>
        public static RemoteSceneInfo[] SceneList(int count)
        {
            List<RemoteSceneInfo> scenes = new List<RemoteSceneInfo>(count);
            for (int i = 0; i < count; i++)
            {
                scenes.Add(new RemoteSceneInfo { index = i, name = HexRSceneNames.DisplayName(i) });
            }

            return scenes.ToArray();
        }

        /// <summary>The floating menu's rule: switching to the scene already open does nothing.</summary>
        public static void LoadBuildScene(int buildIndex)
        {
            if (buildIndex == SceneManager.GetActiveScene().buildIndex)
            {
                return;
            }

            SceneManager.LoadScene(buildIndex);
        }
    }
}
