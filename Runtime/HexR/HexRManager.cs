using System;
using System.Collections;
using System.Collections.Generic;
using HaptGlove;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;
namespace HexR
{
    public class HexRManager : MonoBehaviour
    {
        public static HexRManager Instance { get; private set; }  // ← add here
        public enum Options { OpenXR, MetaOVR } //MRTK not included yet

        /// <summary>
        /// Hook for backend-specific Pressure Controller wiring, invoked by AutoSetup with
        /// (tracker, handRoot, "Left"/"Right"). It exists so the Meta OVR wiring -- which needs
        /// Oculus.Interaction types -- can live in the HexR.Runtime.MetaOVR assembly instead of
        /// here. That assembly is gated on HEXR_META_OVR (auto-defined only when
        /// com.meta.xr.sdk.interaction is installed), so in an OpenXR project it's excluded from
        /// the build entirely and this stays null. Registered from
        /// HexR.MetaOVR.MetaOVRBackendSetup.
        /// </summary>
        public static event Action<PressureTrackerMain, Transform, string> PressureTrackerBackendSetup;
        public Options XRFramework;
        public bool isQuest;

        public HaptGloveHandler leftHand;
        public HaptGloveHandler rightHand;

        public GameObject BluetoothIndicatorL, BluetoothIndicatorR, pumpIndicator_L, pumpIndicator_R, HexRPanel;
        public TextMeshProUGUI RightBtText, LeftBtText;

        // The raw hand rig isn't mirror-symmetric between hands in local space (confirmed --
        // the same offset landed in the wrong place on the right hand), and different
        // fingers on the same hand aren't interchangeable either (confirmed -- right index
        // needed its own value, not the shared one used for Middle/Ring/Little). So every
        // finger on every hand gets its own tunable center instead of one shared value.
        [System.Serializable]
        public class FingertipCenters
        {
            public Vector3 Thumb = Vector3.zero;
            public Vector3 Index = Vector3.zero;
            public Vector3 Middle = Vector3.zero;
            public Vector3 Ring = Vector3.zero;
            public Vector3 Little = Vector3.zero;

            public Vector3 Get(HapticFingerTrigger.FingerType finger)
            {
                switch (finger)
                {
                    case HapticFingerTrigger.FingerType.Thumb: return Thumb;
                    case HapticFingerTrigger.FingerType.Index: return Index;
                    case HapticFingerTrigger.FingerType.Middle: return Middle;
                    case HapticFingerTrigger.FingerType.Ring: return Ring;
                    case HapticFingerTrigger.FingerType.Little: return Little;
                    default: return Vector3.zero;
                }
            }
        }

        [Tooltip("Radius of the sphere trigger colliders Auto Setup adds to each fingertip on the raw tracked hand.")]
        public float FingertipColliderRadius = 0.008f;

        public FingertipCenters LeftFingertipCenters = new FingertipCenters
        {
            Thumb = Vector3.zero,
            Index = new Vector3(0.0102788536f, 0.004925685f, 0.00146568683f),
            Middle = new Vector3(0.0102788536f, 0.004925685f, 0.00146568683f),
            Ring = new Vector3(0.0102788536f, 0.004925685f, 0.00146568683f),
            Little = new Vector3(0.0102788536f, 0.004925685f, 0.00146568683f),
        };

        public FingertipCenters RightFingertipCenters = new FingertipCenters
        {
            Thumb = Vector3.zero,
            Index = new Vector3(-0.00710000005f, -0.00419999985f, 0f),
            Middle = new Vector3(-0.0102788536f, -0.004925685f, 0.00146568683f),
            Ring = new Vector3(-0.0102788536f, -0.004925685f, 0.00146568683f),
            Little = new Vector3(-0.0102788536f, -0.004925685f, 0.00146568683f),
        };

        [Tooltip("Local-space size (x/y/z) of the box trigger collider Auto Setup adds to the palm on the raw tracked hand.")]
        public Vector3 PalmColliderSize = new Vector3(0.06f, 0.017f, 0.065f);

        // Merged in from HaptGloveUI (2026-07-30) -- tracks which hand's button most
        // recently initiated a connection, for UI wired via ConnectLeftBT/ConnectRightBT.

        // A connect request counts as in flight from the button press until the glove
        // connects, fails, or drops. Pressing again while one is running starts a second
        // scan on the same BluetoothHelper, which leaves it searching indefinitely rather
        // than connecting twice -- so extra presses are dropped instead of queued.
        private bool leftConnectInFlight, rightConnectInFlight;
        private int lastBeginConnectFrame = -1;

        // Only one hand may scan at a time. The Android plugin keeps its "am I scanning"
        // flag and its discovered-device list in *static* fields shared by every helper
        // instance, so a second scan started while the first is running both fails to start
        // and clears the results the first one was relying on. A queued hand is started when
        // the running one reaches a terminal outcome.
        private bool hasQueuedConnect;
        private HaptGloveHandler.HandType queuedConnectHand;

        /// <summary>
        /// Claims the connect slot for one hand. Returns false when a request is already
        /// running, in which case the caller should do nothing.
        /// </summary>
        public bool BeginConnect(HaptGloveHandler.HandType hand)
        {
            bool inFlight = hand == HaptGloveHandler.HandType.Left ? leftConnectInFlight : rightConnectInFlight;
            if (inFlight)
            {
                // The panel prefab wires its buttons to HaptGloveUI and this class adds its own
                // listener too, so one press legitimately arrives here twice in the same frame.
                // Only a genuinely separate press is worth a log line.
                if (Time.frameCount != lastBeginConnectFrame)
                {
                    Debug.Log("[HexR] A " + hand + " connect is already running -- ignoring the extra press.");
                }
                return false;
            }
            lastBeginConnectFrame = Time.frameCount;

            // The other hand holds the radio -- queue this one instead of racing it.
            bool otherInFlight = hand == HaptGloveHandler.HandType.Left ? rightConnectInFlight : leftConnectInFlight;
            if (otherInFlight)
            {
                Debug.Log("[HexR] The other hand is still connecting -- queueing " + hand + ".");
                hasQueuedConnect = true;
                queuedConnectHand = hand;
                SetHandText(hand, (hand == HaptGloveHandler.HandType.Left ? "Left" : "Right") + " waiting for the other glove...");
                return false;
            }

            // Which press started which campaign. Both hands retry independently once started,
            // so without this the log cannot tell a fresh press from a retry of an older one --
            // which is what makes a Left failure look like the answer to a Right press.
            Debug.Log("[HexR-BLE] connect slot claimed by " + hand + " (press)");

            if (hand == HaptGloveHandler.HandType.Left)
            {
                leftConnectInFlight = true;
            }
            else
            {
                rightConnectInFlight = true;
            }
            return true;
        }

        // Released on every terminal outcome, so a failed or dropped connection can be
        // retried immediately rather than being locked out until a scene reload.
        private void EndConnect(HaptGloveHandler.HandType hand)
        {
            if (hand == HaptGloveHandler.HandType.Left)
            {
                leftConnectInFlight = false;
            }
            else
            {
                rightConnectInFlight = false;
            }

            if (hasQueuedConnect)
            {
                hasQueuedConnect = false;
                HaptGloveHandler.HandType queued = queuedConnectHand;
                if (queued == HaptGloveHandler.HandType.Left)
                {
                    ConnectLeftBT();
                }
                else
                {
                    ConnectRightBT();
                }
            }
        }

        // One place that knows which label belongs to which hand, so the permission and
        // queueing paths below don't each repeat the null check.
        private void SetHandText(HaptGloveHandler.HandType hand, string message)
        {
            TextMeshProUGUI label = hand == HaptGloveHandler.HandType.Left ? LeftBtText : RightBtText;
            if (label != null)
            {
                label.text = message;
            }
        }

        /// <summary>
        /// Raised when a connect attempt ends without a live connection, so UI that latched
        /// on the way in (the panel's connect toggles) can unlatch.
        /// </summary>
        public static event Action<HaptGloveHandler.HandType> ConnectAttemptEnded;

        public void ConnectRightBT()
        {
            if (!BeginConnect(HaptGloveHandler.HandType.Right))
            {
                return;
            }

            RightBtText.text = "Searching for HexR Right…";
            StartCoroutine(ConnectWhenPermitted(HaptGloveHandler.HandType.Right));
        }

        public void ConnectLeftBT()
        {
            if (!BeginConnect(HaptGloveHandler.HandType.Left))
            {
                return;
            }

            LeftBtText.text = "Searching for HexR Left…";
            StartCoroutine(ConnectWhenPermitted(HaptGloveHandler.HandType.Left));
        }

        // Android 12 (API 31) moved BLE scanning and connecting behind runtime permissions.
        // Declaring them in the manifest is not enough -- without an explicit request,
        // startScan() returns no results at all and connectGatt() throws SecurityException,
        // and neither surfaces as an error. It looks exactly like "no glove nearby", which is
        // why the old failure text could only guess at permissions. Nothing else asks for
        // them: the bundled Java layer only knows the pre-Android-12 spellings
        // (BLUETOOTH/BLUETOOTH_ADMIN plus location), so the package has to.
        //
        // Which names to ask for depends on the platform version, and asking for the wrong
        // ones fails silently rather than loudly. Below API 31 those two names do not exist:
        // HasUserAuthorizedPermission answers false forever and RequestUserPermission shows no
        // dialog, so the poll below ran out its 60-second ceiling and then refused the connect.
        // That is what stopped PICO 4 -- Android 10, API 29 -- from ever reaching BTConnection,
        // while Quest, on Android 12 or newer, was unaffected. What API 23-30 gates a BLE scan
        // on is location, so that is what gets asked for there.
        private static string[] RequiredBluetoothPermissions()
        {
            return AndroidApiLevel() >= 31
                ? new[] { "android.permission.BLUETOOTH_SCAN", "android.permission.BLUETOOTH_CONNECT" }
                : new[] { "android.permission.ACCESS_FINE_LOCATION" };
        }

        private static int AndroidApiLevel()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (AndroidJavaClass version = new AndroidJavaClass("android.os.Build$VERSION"))
                {
                    return version.GetStatic<int>("SDK_INT");
                }
            }
            catch (Exception e)
            {
                // If the version cannot be read, assume the stricter modern set: asking for a
                // permission the device ignores is harmless, skipping one it enforces is not.
                Debug.LogWarning("[HexR] Could not read Build.VERSION.SDK_INT (" + e.Message
                    + ") -- assuming Android 12 or newer for Bluetooth permissions.");
                return 31;
            }
#else
            return 0;
#endif
        }

        /// <summary>"android.permission.ACCESS_FINE_LOCATION" reads as "Location" on a button.</summary>
        private static string ShortPermissionName(string permission)
        {
            return permission.EndsWith("ACCESS_FINE_LOCATION") ? "Location" : "Bluetooth";
        }

        private IEnumerator ConnectWhenPermitted(HaptGloveHandler.HandType hand)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            foreach (string permission in RequiredBluetoothPermissions())
            {
                if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission))
                {
                    continue;
                }

                UnityEngine.Android.Permission.RequestUserPermission(permission);

                // No completion callback is guaranteed on every device/runtime combination,
                // so poll for the answer instead, with a ceiling so a dialog the user never
                // answers can't wedge the connect slot shut forever.
                float deadline = Time.unscaledTime + 60f;
                while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission)
                    && Time.unscaledTime < deadline)
                {
                    yield return null;
                }

                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission))
                {
                    Debug.LogWarning("[HexR] " + permission + " was not granted -- BLE scanning "
                        + "will silently find nothing until it is. Grant it in the system app settings.");
                    SetHandText(hand, (hand == HaptGloveHandler.HandType.Left ? "Left" : "Right")
                        + " blocked — allow " + ShortPermissionName(permission)
                        + " in system settings, then try again");
                    EndConnect(hand);
                    HexRPanel?.SetActive(true);
                    ConnectAttemptEnded?.Invoke(hand);
                    yield break;
                }
            }
#else
            yield return null;
#endif

            // On Unity's thread. BTConnection starts coroutines and reads Time, and only the
            // two plugin calls that need a Looper hop to the Android UI thread -- the handler
            // does that itself (see HaptGloveHandler.PluginConnect / PluginScan). Marshalling
            // the whole call, as this used to, made the first StartCoroutine throw on the UI
            // thread before the scan was ever started.
            HaptGloveHandler handler = hand == HaptGloveHandler.HandType.Left ? leftHand : rightHand;
            if (handler != null)
            {
                handler.BTConnection();
            }
        }

        void Start()
        {
            if (isQuest)
            {
                leftHand.isQuest = true;
                rightHand.isQuest = true;
            }
            else
            {
                leftHand.isQuest = false;
                rightHand.isQuest = false;
            }

            leftHand.onBluetoothConnected += HaptGlove_OnConnected;
            leftHand.onBluetoothConnectionFailed += HaptGlove_OnConnectedFailed;
            leftHand.onBluetoothDisconnected += HaptGlove_OnDisconnected;
            leftHand.onPumpAction += HaptGlove_OnPumpAction;

            rightHand.onBluetoothConnected += HaptGlove_OnConnected;
            rightHand.onBluetoothConnectionFailed += HaptGlove_OnConnectedFailed;
            rightHand.onBluetoothDisconnected += HaptGlove_OnDisconnected;
            rightHand.onPumpAction += HaptGlove_OnPumpAction;

            //Add all layers that you want to interact with HaptGlove
            AddHaptGloveInteractableLayer("HaptGloveInteractable");
        }
        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            // This rig survives scene loads. The Meta hands it mirrors do not -- every scene
            // carries its own OVRCameraRig, so the moment a new scene loads, the hand roots
            // (and the fingertip trigger colliders that live on them) are destroyed, and the
            // gloves go silent for the rest of the session.
            // Re-point everything at the incoming scene's rig instead.
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            // Guarded so a duplicate rig destroying itself above -- which never subscribed --
            // can't tear down the surviving instance's hook.
            if (Instance == this)
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // An additive load leaves the previous scene's rig alive and tracking, so there is
            // nothing to recover from -- and re-pointing at an identically-named copy in the
            // newly added scene would actively break a working hand.
            if (mode != LoadSceneMode.Single)
            {
                return;
            }

            StartCoroutine(RebindHandsWhenReady());
        }

        // The incoming scene's hand rig isn't necessarily usable on the frame sceneLoaded fires:
        // Meta's HandVisual only swaps the legacy bone rig for the OpenXR one in its own Awake,
        // and in a build the joints can take another frame or two to appear. Retry rather than
        // give up on the first miss -- a missed rebind isn't a dropped frame, it's hands that
        // never move again.
        private IEnumerator RebindHandsWhenReady()
        {
            const float timeoutSeconds = 5f;
            float deadline = Time.unscaledTime + timeoutSeconds;

            while (true)
            {
                if (RebindHandsToCurrentScene())
                {
                    yield break;
                }

                if (Time.unscaledTime >= deadline)
                {
                    Debug.LogWarning("[HexR] Couldn't find a tracked hand rig in the loaded scene within "
                        + timeoutSeconds + "s -- the HexR hands won't mirror the Meta hands here. Does this "
                        + "scene have an OVRCameraRig with hand tracking?");
                    yield break;
                }

                yield return null;
            }
        }

        // Re-points both hands at whatever tracked rig the currently loaded scene provides, and
        // re-adds the fingertip/palm haptics that lived on the old one. Public so it can also be
        // called by hand after building a rig at runtime. Returns true once both hands are
        // driven, so the retry loop above knows when to stop.
        public bool RebindHandsToCurrentScene()
        {
            bool leftOk = RebindHand(leftHand, HaptGloveHandler.HandType.Left);
            bool rightOk = RebindHand(rightHand, HaptGloveHandler.HandType.Right);
            return leftOk && rightOk;
        }

        private bool RebindHand(HaptGloveHandler hand, HaptGloveHandler.HandType handType)
        {
            // Nothing to rebind, and nothing this method can do about it -- ValidateSetup
            // already reports an unassigned hand.
            if (hand == null) return true;

            PhysicsHandTracking legacy = hand.GetComponent<PhysicsHandTracking>();
            if (legacy == null) return true;

            GameObject root = handType == HaptGloveHandler.HandType.Left
                ? FindHandVisualRoot("OpenXRLeftHand", "OculusHand_L", "LeftOVRHand")
                : FindHandVisualRoot("OpenXRRightHand", "OculusHand_R", "RightOVRHand");
            if (root == null) return false;

            legacy.handRoot = root.transform;

            // Readiness test for the incoming rig, replacing the old one that leaned on the
            // mirror having mapped. This asks the question the work below actually depends on:
            // can the raw hand's joints be resolved yet? HandVisual builds them in its own
            // Awake and a build can need another frame or two, so a miss here means "not yet",
            // not "never" -- the caller retries.
            if (legacy.ResolveRawPalmJoint() == null)
            {
                return false;
            }

            // The fingertip/palm trigger colliders Auto Setup adds sit on the raw tracked hand,
            // so they were destroyed with the previous scene's rig. Without re-adding them the
            // gloves go silent -- and scenes Auto Setup was never run on (2.Hosptal Tutorial and
            // 3.Water Effects have none authored at all) have never had them in the first place.
            AutoAddFingerHapticsForHand(this, hand, handType);
            AutoSetupPressureController(this, hand, handType);

            // Counted from the rig itself rather than assumed, because "Auto Setup ran" and
            // "the hand can actually fire haptics" are different claims -- and the gap between
            // them is invisible unless something says so out loud. Six is correct: five
            // fingertips plus the palm.
            int triggers = legacy.handRoot != null
                ? legacy.handRoot.GetComponentsInChildren<HapticFingerTrigger>(true).Length
                : 0;
            string pressure = GameObject.Find((handType == HaptGloveHandler.HandType.Left ? "Left" : "Right")
                + " Pressure Controller") != null ? "found" : "MISSING";

            Debug.Log("[HexR] Rebound " + handType + " hand to " + root.name + " in scene "
                + SceneManager.GetActiveScene().name + " -- " + triggers + " haptic trigger(s) on the tracked hand"
                + ", Pressure Controller " + pressure + ".");
            return true;
        }

        private void HaptGlove_OnConnected(HaptGloveHandler.HandType hand)
        {
            EndConnect(hand);

            if (hand == HaptGloveHandler.HandType.Left)
            {
                BluetoothIndicatorL?.SetActive(true);
                if (LeftBtText != null)
                {
                    LeftBtText.text = "Left connected — starting up…";
                }
                StartCoroutine(Pump(leftHand.GetComponent<HaptGloveHandler>()));
                StopBatteryPolling(hand);
                leftBatteryPoll = StartCoroutine(TriggerFunctionEvery8Seconds("Left"));
            }
            else if (hand == HaptGloveHandler.HandType.Right)
            {
                BluetoothIndicatorR?.SetActive(true);
                if (RightBtText != null)
                {
                    RightBtText.text = "Right connected — starting up…";
                }
                StartCoroutine(Pump(rightHand.GetComponent<HaptGloveHandler>()));
                StopBatteryPolling(hand);
                rightBatteryPoll = StartCoroutine(TriggerFunctionEvery8Seconds("Right"));
            }

        }
        // Held so the loop below can be stopped when the glove goes away. It never used to be:
        // the coroutine is a bare while(true) started on every connect and stopped by nothing,
        // so each reconnect stacked another copy, all writing the same label and polling a
        // handler that may no longer have a link.
        private Coroutine leftBatteryPoll, rightBatteryPoll;

        private void StopBatteryPolling(HaptGloveHandler.HandType hand)
        {
            if (hand == HaptGloveHandler.HandType.Left)
            {
                if (leftBatteryPoll != null)
                {
                    StopCoroutine(leftBatteryPoll);
                    leftBatteryPoll = null;
                }
            }
            else
            {
                if (rightBatteryPoll != null)
                {
                    StopCoroutine(rightBatteryPoll);
                    rightBatteryPoll = null;
                }
            }
        }

        IEnumerator TriggerFunctionEvery8Seconds(String LeftOrRight)
        {
            while (true) // Infinite loop to keep the coroutine running
            {
                // Call your function here
                BatteryState(LeftOrRight);

                // Wait for 8 seconds before continuing the loop
                yield return new WaitForSeconds(8f);
            }
        }
        private void BatteryState(String LeftOrRight)
        {
            if(LeftOrRight == "Right")
            {
                float BatteryLevel = rightHand.GetBatteryLevel();
                if(BatteryLevel == 0)
                {
                    RightBtText.text = "Right ready";
                }
                else
                {
                    RightBtText.text = "Right ready · " + Math.Round(BatteryLevel * 100) + "%";
                }
            }
            else if(LeftOrRight == "Left")
            {
                float BatteryLevel = leftHand.GetBatteryLevel();
                if (BatteryLevel == 0)
                {
                    LeftBtText.text = "Left ready";
                }
                else
                {
                    LeftBtText.text = "Left ready · " + Math.Round(BatteryLevel * 100) + "%";
                }

            }

        }
        IEnumerator Pump(HaptGloveHandler haptGloveHandler)
        {
            // Wait for the specified delay time
            yield return new WaitForSeconds(2f);

            haptGloveHandler.AirPressureSourceControl();
        }

        private void HaptGlove_OnConnectedFailed(HaptGloveHandler.HandType hand)
        {
            EndConnect(hand);

            if (hand == HaptGloveHandler.HandType.Left)
            {
                BluetoothIndicatorL?.SetActive(false);
                if(LeftBtText != null)
                {
                    LeftBtText.text = "Left not found — check Bluetooth permissions, then try again";
                }
            }
            else if (hand == HaptGloveHandler.HandType.Right)
            {
                BluetoothIndicatorR?.SetActive(false);
                if (RightBtText != null)
                {
                    RightBtText.text = "Right not found — check Bluetooth permissions, then try again";
                }
            }
            StopBatteryPolling(hand);
            HexRPanel?.SetActive(true);
            ConnectAttemptEnded?.Invoke(hand);
        }

        private void HaptGlove_OnDisconnected(HaptGloveHandler.HandType hand)
        {
            EndConnect(hand);

            if (hand == HaptGloveHandler.HandType.Left)
            {
                BluetoothIndicatorL?.SetActive(false);
                if(LeftBtText!=null)
                {
                    LeftBtText.text = "Left disconnected — reconnecting…";
                }
            }
            else if (hand == HaptGloveHandler.HandType.Right)
            {
                BluetoothIndicatorR?.SetActive(false);
                if (RightBtText != null)
                {
                    RightBtText.text = "Right disconnected — reconnecting…";
                }
            }
            StopBatteryPolling(hand);
            HexRPanel?.SetActive(true);
            // Deliberately no ConnectAttemptEnded here: the handler reopens its own connect
            // campaign on a drop and retries with backoff, so the connect toggle should stay
            // latched. It unlatches from HaptGlove_OnConnectedFailed, which is what fires when
            // that campaign finally gives up.
        }

        private void HaptGlove_OnPumpAction(HaptGloveHandler.HandType hand, bool state)
        {
            if (hand == HaptGloveHandler.HandType.Left)
            {
                if (LeftBtText != null)
                {
                    LeftBtText.text = "Left ready";
                }
                if (state)
                    pumpIndicator_L?.SetActive(true);
                else
                    pumpIndicator_L?.SetActive(false);
            }
            else if (hand == HaptGloveHandler.HandType.Right)
            {
                if (RightBtText != null)
                {
                    RightBtText.text = "Right ready";
                }
                if (state)
                    pumpIndicator_R?.SetActive(true);
                else
                    pumpIndicator_R?.SetActive(false);
            }
        }

        public void AddHaptGloveInteractableLayer(string layerName)
        {
            leftHand.hapticsInteratableLayers.Add(LayerMask.NameToLayer(layerName));
            rightHand.hapticsInteratableLayers.Add(LayerMask.NameToLayer(layerName));
        }



        // Everything from here down is deliberately outside the UNITY_EDITOR fence, even though
        // Auto Setup is its main caller: this rig is DontDestroyOnLoad and the tracked Meta hands
        // are not, so the same wiring has to be redone at runtime against each newly loaded
        // scene's rig (see OnSceneLoaded/RebindHandsToCurrentScene). Only the SetDirty calls --
        // which exist purely so an Editor-time run gets saved into the scene -- stay fenced.

        // Returns the first candidate name that exists in the scene, preferring a copy under a
        // "*Synthetic*" parent when the rig has one -- the synthetic hand carries the
        // grab-adjusted pose the rest of the HexR rig follows, and it's the object the previous
        // OculusHand_L/R lookup happened to land on. Searches inactive objects too, since the
        // OpenXR hand root is authored inactive in rigs that predate the v201 SDK.
        internal static GameObject FindHandVisualRoot(params string[] candidateNames)
        {
            GameObject[] all = HexRCompat.FindAll<GameObject>(true);
            foreach (string name in candidateNames)
            {
                GameObject fallback = null;
                foreach (GameObject go in all)
                {
                    if (go.name != name) continue;
                    if (IsUnderSyntheticHand(go.transform))
                    {
                        return go;
                    }
                    if (fallback == null)
                    {
                        fallback = go;
                    }
                }
                if (fallback != null)
                {
                    return fallback;
                }
            }
            return null;
        }

        private static bool IsUnderSyntheticHand(Transform t)
        {
            for (Transform c = t; c != null; c = c.parent)
            {
                if (c.name.IndexOf("Synthetic", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        internal static void AutoAddFingerHaptics(HexRManager controller)
        {
            AutoAddFingerHapticsForHand(controller, controller.leftHand, HaptGloveHandler.HandType.Left);
            AutoAddFingerHapticsForHand(controller, controller.rightHand, HaptGloveHandler.HandType.Right);
        }

        internal static readonly HapticFingerTrigger.FingerType[] Fingers = new[]
        {
            HapticFingerTrigger.FingerType.Thumb, HapticFingerTrigger.FingerType.Index,
            HapticFingerTrigger.FingerType.Middle, HapticFingerTrigger.FingerType.Ring,
            HapticFingerTrigger.FingerType.Little
        };

        internal static void AutoSetupPressureControllers(HexRManager controller)
        {
            AutoSetupPressureController(controller, controller.leftHand, HaptGloveHandler.HandType.Left);
            AutoSetupPressureController(controller, controller.rightHand, HaptGloveHandler.HandType.Right);
        }

        private static void AutoSetupPressureController(HexRManager controller, HaptGloveHandler hand, HaptGloveHandler.HandType handType)
        {
            string label = handType == HaptGloveHandler.HandType.Left ? "Left" : "Right";

            GameObject controllerObj = GameObject.Find(label + " Pressure Controller");
            if (controllerObj == null)
            {
                // Already flagged by ValidateSetup's "Pressure Controller present" check --
                // not created here since there's nothing sensible to attach it to on its own.
                return;
            }

            PressureTrackerMain pressureTracker = controllerObj.GetComponent<PressureTrackerMain>();
            if (pressureTracker == null)
            {
                Debug.LogWarning("[HexR] AutoSetup: \"" + label + " Pressure Controller\" has no PressureTrackerMain component -- skipping its setup.");
                return;
            }

            pressureTracker.handType = handType == HaptGloveHandler.HandType.Left ? PressureTrackerMain.HandType.Left : PressureTrackerMain.HandType.Right;

            // Hand-near gating reads interactor types this assembly deliberately can't see --
            // Oculus.Interaction on Meta, the XR Interaction Toolkit on OpenXR. Referencing either
            // here is what used to make the package unusable in a project without that SDK. Both
            // wirings live in their own assembly (HexR.Runtime.MetaOVR / HexR.Runtime.OpenXR) and
            // register themselves through PressureTrackerBackendSetup, each gated on its SDK being
            // present, so an absent backend is simply not compiled.
            //
            // Invoked for either framework: an OpenXR rig needs this just as much as a Meta one,
            // and only calling it for Meta is what left OpenXR projects hand-placing their own
            // hand-near source on every Pressure Controller. Each backend checks the rig's
            // framework before acting, so a project with both SDKs installed compiles both
            // assemblies without either wiring the other's rig.
            if (hand != null)
            {
                PhysicsHandTracking tracking = hand.GetComponent<PhysicsHandTracking>();
                if (tracking != null && tracking.handRoot != null)
                {
                    Action<PressureTrackerMain, Transform, string> backendSetup = PressureTrackerBackendSetup;
                    if (backendSetup != null)
                    {
                        backendSetup(pressureTracker, tracking.handRoot, label);
                    }
                    else if (controller.XRFramework == Options.MetaOVR)
                    {
                        Debug.LogWarning("[HexR] AutoSetup: this rig is set to Meta OVR but the Meta Interaction SDK "
                            + "(com.meta.xr.sdk.interaction) isn't installed, so " + label + " Pressure Controller's "
                            + "grab/poke gating can't be wired. Install it, or switch this rig to OpenXR via "
                            + "HexR > Create HexR Rig > Open XR.");
                    }
                    else
                    {
                        Debug.LogWarning("[HexR] AutoSetup: this rig is set to OpenXR but the XR Interaction Toolkit "
                            + "(com.unity.xr.interaction.toolkit) isn't installed, so " + label + " Pressure "
                            + "Controller's grab/poke gating can't be wired. Install it, or put a ProximityCheck "
                            + "volume on each object you want felt.");
                    }
                }
            }

#if UNITY_EDITOR
            EditorUtility.SetDirty(pressureTracker);
#endif
        }

        // Adds HaptGloveCollidersVisualizer to `target` if it doesn't already have one. Called
        // once, on the manager itself: the visualizer resolves both hands' tracked and ghost
        // roots through HexRManager rather than scanning its own children, so where it sits no
        // longer decides what it can draw. Point the HexR Panel's collider toggle at this one.
        internal static void EnsureColliderVisualizer(GameObject target)
        {
            if (target.GetComponent<HaptGloveCollidersVisualizer>() == null)
            {
                target.AddComponent<HaptGloveCollidersVisualizer>();
#if UNITY_EDITOR
                EditorUtility.SetDirty(target);
#endif
            }
        }

        private static void AutoAddFingerHapticsForHand(HexRManager controller, HaptGloveHandler hand, HaptGloveHandler.HandType handType)
        {
            if (hand == null) return;
            PhysicsHandTracking tracking = hand.GetComponent<PhysicsHandTracking>();
            if (tracking == null) return;

            // No collider visualizer is added here any more. It used to need one per hand root,
            // because it could only draw colliders under its own transform; it now resolves both
            // hands through HexRManager, so a single instance on the manager (see AutoSetup)
            // covers everything. Adding one here as well would just be a second component
            // fighting over the same colliders -- and this method also runs on every scene load
            // via RebindHand, which would have accumulated one per rig.

            foreach (HapticFingerTrigger.FingerType finger in Fingers)
            {
                try
                {
                    Transform joint = tracking.ResolveRawFingerJoint(finger);
                    if (joint == null)
                    {
                        Debug.LogWarning("[HexR] AutoSetup: couldn't resolve " + handType + " " + finger + " tip joint on the raw hand -- skipping.");
                        continue;
                    }

                    // For the thumb, prefer the rig's own "..._thumb_null" locator (if it has
                    // one) over the tunable center -- it's an authored answer, not a guess.
                    Vector3? centerOverride = null;
                    if (finger == HapticFingerTrigger.FingerType.Thumb)
                    {
                        Transform thumbMarker = tracking.ResolveRawThumbCenterMarker();
                        if (thumbMarker != null)
                        {
                            centerOverride = joint.InverseTransformPoint(thumbMarker.position);
                        }
                    }

                    AddOrConfigureFingerHaptics(joint.gameObject, controller, handType, finger, centerOverride);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[HexR] AutoSetup: failed to set up " + handType + " " + finger + " haptics -- " + e.Message);
                }
            }

            try
            {
                Transform palm = tracking.ResolveRawPalmJoint();
                if (palm == null)
                {
                    Debug.LogWarning("[HexR] AutoSetup: couldn't resolve " + handType + " palm joint on the raw hand -- skipping.");
                }
                else
                {
                    AddOrConfigureFingerHaptics(palm.gameObject, controller, handType, HapticFingerTrigger.FingerType.Palm, null);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[HexR] AutoSetup: failed to set up " + handType + " palm haptics -- " + e.Message);
            }
        }

        // Never touches a collider that's already there (respects manual tuning/re-runs).
        // HapticFingerTrigger is added if missing, otherwise just has its config refreshed --
        // so re-running Auto Setup after reassigning hands fixes stale wiring.
        private static void AddOrConfigureFingerHaptics(GameObject target, HexRManager controller, HaptGloveHandler.HandType handType, HapticFingerTrigger.FingerType finger, Vector3? centerOverride)
        {
            if (target.GetComponent<Collider>() == null)
            {
                if (finger == HapticFingerTrigger.FingerType.Palm)
                {
                    BoxCollider box = target.AddComponent<BoxCollider>();
                    box.isTrigger = true;
                    box.size = controller.PalmColliderSize;
                }
                else
                {
                    SphereCollider sphere = target.AddComponent<SphereCollider>();
                    sphere.isTrigger = true;
                    sphere.radius = controller.FingertipColliderRadius;
                    if (centerOverride.HasValue)
                    {
                        sphere.center = centerOverride.Value;
                    }
                    else
                    {
                        FingertipCenters centers = handType == HaptGloveHandler.HandType.Left
                            ? controller.LeftFingertipCenters
                            : controller.RightFingertipCenters;
                        sphere.center = centers.Get(finger);
                    }
                }
            }

            // Without this, none of the colliders above can ever raise a trigger event.
            //
            // Unity only calls OnTriggerEnter/Stay/Exit when at least one of the two colliders
            // belongs to a Rigidbody. The haptic zones (SpecialHaptics, HexRGrabbable) have
            // none, and neither does Meta's tracked hand -- so once detection moved off the
            // ghost rig, which carried 12 Rigidbodies alongside its 12 HapticFingerTriggers,
            // onto the raw hand, every collision-driven haptic in the project went silent. The
            // colliders are all present and correctly placed; they simply never fire, which is
            // why this reads as "the collider visualizer shows green but nothing happens".
            //
            // One kinematic body per fingertip rather than a single one on the hand root: these
            // joints move independently, and a compound collider whose children move relative to
            // the body has to be rebuilt every frame. Safe to add here because Meta's hand
            // prefabs ship no colliders of their own, so this body owns only the collider added
            // above.
            //
            // Through HapticFingerTrigger's own helper rather than configured here, so the
            // settings exist in exactly one place. HapticFingerTrigger.Awake calls the same
            // thing, which covers colliders that arrive by any route other than Auto Setup.
            HapticFingerTrigger.EnsureHapticBody(target);

            HapticFingerTrigger trigger = target.GetComponent<HapticFingerTrigger>();
            if (trigger == null)
            {
                trigger = target.AddComponent<HapticFingerTrigger>();
            }
            trigger.handType = handType == HaptGloveHandler.HandType.Left ? HapticFingerTrigger.HandType.Left : HapticFingerTrigger.HandType.Right;
            trigger.fingertype = finger;
            trigger.HexrLeftOrRight = handType == HaptGloveHandler.HandType.Left ? controller.leftHand.gameObject : controller.rightHand.gameObject;

#if UNITY_EDITOR
            EditorUtility.SetDirty(target);
#endif
        }


    }
}
