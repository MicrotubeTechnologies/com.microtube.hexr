using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace HexR
{
    /// <summary>
    /// Lets a phone on the same Wi-Fi find this headset, connect and disconnect its gloves, watch
    /// their state, and see the headset view. The phone side is the HexR Tester app's Headsets tab;
    /// the wire protocol is Documentation~/RemoteProtocol.md.
    ///
    /// Opt-in: add it with HexR > Add HexR Remote. Nothing in the package starts it otherwise,
    /// because it opens network ports and can ask to share what the wearer sees.
    ///
    /// Nothing here bypasses the HexR API: a phone's "connect left" is HexRManager.Connect, run on
    /// the main thread exactly as the floating menu's button would.
    /// </summary>
    [AddComponentMenu("HexR/HexR Remote")]
    [DisallowMultipleComponent]
    public class HexRRemote : MonoBehaviour
    {
        /// <summary>The package version this file shipped in, reported to phones. A test keeps it equal to package.json.</summary>
        public const string HexRVersion = "0.10.2";

        public static HexRRemote Instance { get; private set; }

        [Header("What a phone may do")]
        [Tooltip("Connect and disconnect the gloves, and release their haptics.")]
        public bool allowGloveControl = true;

        [Tooltip("Share the headset view with the phone. The wearer still has to accept Android's prompt in the headset each session.")]
        public bool allowVideo = true;

        [Tooltip("Show the phone this app's build scenes and let it switch between them. Off by default, like the floating menu's scene switcher.")]
        public bool exposeScenes;

        [Header("Sharing the view")]
        [Tooltip("Encoded size. Quest's compositor delivers a square view, so a square size wastes nothing. Rounded to a multiple of 16.")]
        public int videoWidth = 1024;

        public int videoHeight = 1024;

        [Range(500, 20000)]
        public int videoBitrateKbps = 4000;

        [Range(10, 60)]
        public int videoFps = 30;

        [Tooltip("End the view share if this app stays in the background this long, so it never keeps capturing another app or the home screen.")]
        public int stopVideoAfterBackgroundSeconds = 20;

        [Header("Network")]
        [Tooltip("UDP port phones listen on for this headset's announcement.")]
        public int discoveryPort = RemoteProtocol.DefaultDiscoveryPort;

        [Tooltip("TCP port for commands and status. If it is taken, the next free one is used and announced.")]
        public int controlPort = RemoteProtocol.DefaultControlPort;

        [Tooltip("TCP port for the video. If it is taken, the next free one is used and reported.")]
        public int videoPort = RemoteProtocol.DefaultVideoPort;

        [Range(1, 20)]
        public float statusPerSecond = 5f;

        /// <summary>Phones with a control connection open right now.</summary>
        public int PhonesConnected => server != null ? server.ClientCount : 0;

        /// <summary>Phones receiving the headset view right now.</summary>
        public int PhonesWatching => lastStream.viewers;

        /// <summary>
        /// Requests older than this when Update gets to them are refused: the app was paused or
        /// hitching, and a scene change or a capture prompt arriving long after the tap surprises
        /// whoever is in the headset by then.
        /// </summary>
        private const double StaleRequestSeconds = 5;

        private static readonly Dictionary<string, CommandEntry> commands = new Dictionary<string, CommandEntry>();
        private static readonly List<string> commandOrder = new List<string>();

        private RemoteServer server;
        private RemoteDiscovery discovery;
        private IRemoteStream stream;
        private RemoteCommands handler;
        private readonly RemoteHaptics haptics = new RemoteHaptics();
        private readonly List<string> hapticLines = new List<string>();
        private HexRManager foundRig;
        private float nextRigLookup;
        private RemoteStreamStatus lastStream = new RemoteStreamStatus();
        private RemoteAnnounce announce;
        private RemoteSceneInfo[] scenes = new RemoteSceneInfo[0];
        private string deviceId, deviceName, deviceModel, platform, app, appVersion;
        private long startedAt;
        private float nextStatus;
        private bool started;
        private bool paused;
        private int announcedVideoPort = -1;

        private struct CommandEntry
        {
            public string Label;
            public Action Action;
        }

        // ---- app commands ------------------------------------------------------------------------

        /// <summary>
        /// Puts a button for this app on the phone: "Reset demo", "Start heartbeat", anything the
        /// facilitator should be able to do without the headset. The action runs on the main
        /// thread. Registering the same id again replaces it. Safe to call before the remote exists.
        /// </summary>
        public static void RegisterCommand(string id, string label, Action action)
        {
            if (string.IsNullOrEmpty(id) || action == null)
            {
                throw new ArgumentException("A remote command needs an id and an action.");
            }

            if (!commands.ContainsKey(id))
            {
                commandOrder.Add(id);
            }

            commands[id] = new CommandEntry { Label = string.IsNullOrEmpty(label) ? id : label, Action = action };
        }

        public static void UnregisterCommand(string id)
        {
            if (id != null && commands.Remove(id))
            {
                commandOrder.Remove(id);
            }
        }

        internal static bool TryGetCommand(string id, out Action action)
        {
            if (id != null && commands.TryGetValue(id, out CommandEntry entry))
            {
                action = entry.Action;
                return true;
            }

            action = null;
            return false;
        }

        internal static RemoteCommandInfo[] CommandList()
        {
            RemoteCommandInfo[] list = new RemoteCommandInfo[commandOrder.Count];
            for (int i = 0; i < commandOrder.Count; i++)
            {
                list[i] = new RemoteCommandInfo { id = commandOrder[i], label = commands[commandOrder[i]].Label };
            }

            return list;
        }

        /// <summary>
        /// Projects that skip the domain reload on entering Play mode keep statics between runs:
        /// last run's instance and commands pointing at destroyed objects.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
            commands.Clear();
            commandOrder.Clear();
        }

        // ---- lifecycle ---------------------------------------------------------------------------

        private void Awake()
        {
            // Its own check rather than relying on HexRManager's: on a duplicate rig this Awake can
            // run before the manager's Awake gets round to removing the copy. Quietly: every scene of
            // a multi-scene app carries its own rig and remote, and the first scene's one is kept on
            // every load after it, so this is the normal case rather than a mistake.
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;

            // Under the HexR rig it already survives scene loads with the manager. On its own it has
            // to do that itself, or every scene change would drop every phone.
            if (transform.parent == null)
            {
                DontDestroyOnLoad(gameObject);
            }
        }

        private void Start()
        {
            if (Instance != this)
            {
                return;
            }

            started = true;
            Begin();
        }

        private void OnEnable()
        {
            if (started && Instance == this && server == null)
            {
                Begin();
            }
        }

        private void OnDisable()
        {
            Shutdown();
        }

        private void OnDestroy()
        {
            Shutdown();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void OnApplicationQuit()
        {
            stream?.Stop();
        }

        private void OnApplicationPause(bool pause)
        {
            paused = pause;
            if (server != null)
            {
                server.Paused = pause;

                // The last thing phones hear before Update stops: "paused", so they show the headset
                // as asleep rather than gone. The server keeps repeating it as a heartbeat.
                PublishStatus();
            }
        }

        private void Begin()
        {
            deviceId = SystemInfo.deviceUniqueIdentifier;
            deviceName = SystemInfo.deviceName;
            deviceModel = SystemInfo.deviceModel;
            platform = PlatformName();
            app = Application.productName;
            appVersion = Application.version;
            startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            scenes = exposeScenes ? RemoteCommands.SceneList(SceneManager.sceneCountInBuildSettings) : new RemoteSceneInfo[0];

            stream = RemoteStreams.Create(this);
            handler = new RemoteCommands(ResolveManager, stream, () => SceneManager.sceneCountInBuildSettings,
                RemoteCommands.LoadBuildScene);

            try
            {
                server = new RemoteServer();
                server.Start(controlPort);

                discovery = new RemoteDiscovery();
                discovery.Start(discoveryPort);
            }
            catch (Exception e)
            {
                Debug.LogError("[HexR] Remote: could not open its network ports, so phones cannot reach this app: " + e.Message, this);
                Shutdown();
                return;
            }

            announce = new RemoteAnnounce
            {
                id = deviceId,
                name = deviceName,
                model = deviceModel,
                platform = platform,
                app = app,
                appId = Application.identifier,
                hexrVersion = HexRVersion,
                control = server.Port,
                startedAt = startedAt,
            };
            UpdateAnnounce(videoPort);
            server.SetHapticsSnapshot(haptics.Snapshot());

#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
#endif
            Debug.Log("[HexR] Remote: phones can connect on port " + server.Port + " (announcing on UDP " + discoveryPort + ").", this);
        }

        private void Shutdown()
        {
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
#endif
            stream?.Stop();
            server?.Dispose();
            discovery?.Dispose();
            server = null;
            discovery = null;
            lastStream = new RemoteStreamStatus();
        }

        private void Update()
        {
            if (server == null)
            {
                return;
            }

            handler.AllowGloveControl = allowGloveControl;
            handler.AllowVideo = allowVideo;
            handler.ExposeScenes = exposeScenes;

            // A handful per frame is plenty for a person tapping a phone, and bounds the frame cost
            // if something floods the port.
            for (int i = 0; i < 16 && server.TryTake(out RemoteInbound inbound); i++)
            {
                if ((DateTime.UtcNow - inbound.ReceivedUtc).TotalSeconds > StaleRequestSeconds)
                {
                    inbound.Client.Send(RemoteProtocol.ErrorLine(inbound.Request.type,
                        "The headset was busy and this request expired. Try again."));
                    continue;
                }

                string error = handler.Handle(inbound.Request);
                if (error != null)
                {
                    inbound.Client.Send(RemoteProtocol.ErrorLine(inbound.Request.type, error));
                }
                else
                {
                    // Show the effect straight away rather than up to one status interval later.
                    nextStatus = 0f;
                }
            }

            if (Time.unscaledTime >= nextStatus)
            {
                nextStatus = Time.unscaledTime + 1f / Mathf.Max(1f, statusPerSecond);
                PublishStatus();
            }
        }

        /// <summary>
        /// After every script's Update, so a haptic command sent this frame goes out this frame,
        /// stamped with the time the frame showing it was made.
        /// </summary>
        private void LateUpdate()
        {
            if (server == null)
            {
                return;
            }

            hapticLines.Clear();
            haptics.Poll(RigForHaptics(), hapticLines);
            if (hapticLines.Count == 0)
            {
                return;
            }

            foreach (string line in hapticLines)
            {
                server.PublishEvent(line);
            }

            server.SetHapticsSnapshot(haptics.Snapshot());
        }

        /// <summary>
        /// ResolveManager, but at most one search a second while there is no manager: this runs
        /// every frame, and a scene without a rig would otherwise search the whole scene each time.
        /// </summary>
        private HexRManager RigForHaptics()
        {
            if (HexRManager.Instance != null)
            {
                return HexRManager.Instance;
            }

            if (foundRig == null && Time.unscaledTime >= nextRigLookup)
            {
                nextRigLookup = Time.unscaledTime + 1f;
                foundRig = ResolveManager();
            }

            return foundRig;
        }

        private void PublishStatus()
        {
            if (server == null)
            {
                return;
            }

            lastStream = stream.Read();
            if (!allowVideo && lastStream.state != RemoteStreamState.Live)
            {
                lastStream.state = RemoteStreamState.Unsupported;
                lastStream.message = "This app does not share its view.";
            }

            if (lastStream.port > 0 && lastStream.port != announcedVideoPort)
            {
                UpdateAnnounce(lastStream.port);
            }

            Scene active = SceneManager.GetActiveScene();
            RemoteStatus status = new RemoteStatus
            {
                id = deviceId,
                name = deviceName,
                model = deviceModel,
                platform = platform,
                app = app,
                appVersion = appVersion,
                hexrVersion = HexRVersion,
                paused = paused,
                headsetBatteryPct = SystemInfo.batteryLevel >= 0f ? Mathf.RoundToInt(SystemInfo.batteryLevel * 100f) : -1,
                scene = new RemoteSceneInfo
                {
                    index = active.buildIndex,
                    name = active.buildIndex >= 0 ? HexRSceneNames.DisplayName(active.buildIndex) : active.name,
                },
                scenes = exposeScenes ? scenes : new RemoteSceneInfo[0],
                gloves = RemoteCommands.GloveStatus(ResolveManager()),
                stream = lastStream,
                commands = CommandList(),
                allowGloveControl = allowGloveControl,
                allowVideo = allowVideo,
                exposeScenes = exposeScenes,
                controllers = server.ClientCount,
                haptics = true,
                clockUs = RemoteClock.NowUs(),
            };

            server.PublishStatus(RemoteProtocol.ToLine(status));
        }

        private void UpdateAnnounce(int port)
        {
            announcedVideoPort = port;
            announce.video = port;
            discovery.SetAnnounce(RemoteProtocol.ToLine(announce));
            server.SetWelcome(RemoteProtocol.ToLine(new RemoteWelcome
            {
                id = deviceId,
                control = server.Port,
                video = port,
            }));
        }

        /// <summary>
        /// The same lookup the floating menu uses: a domain reload in Play mode clears the static
        /// while the manager lives on in DontDestroyOnLoad.
        /// </summary>
        private static HexRManager ResolveManager()
        {
            HexRManager manager = HexRManager.Instance;
            if (manager == null)
            {
                manager = FindAnyObjectByType<HexRManager>(FindObjectsInactive.Include);
            }

            return manager;
        }

        /// <summary>
        /// "quest", "pico", "android", "editor" or "desktop". On Android, SystemInfo.deviceModel is
        /// "manufacturer model", e.g. "Oculus Quest 3" or "Pico A8110".
        /// </summary>
        private static string PlatformName()
        {
            if (Application.isEditor)
            {
                return "editor";
            }

            string model = SystemInfo.deviceModel.ToLowerInvariant();
            if (model.Contains("oculus") || model.Contains("quest") || model.Contains("meta"))
            {
                return "quest";
            }

            if (model.Contains("pico"))
            {
                return "pico";
            }

            return Application.platform == RuntimePlatform.Android ? "android" : "desktop";
        }
    }
}
