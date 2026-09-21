using System.Collections.Generic;
using HexR;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// The HexR menu as a panel floating in the room, grabbed by its header and left wherever you
/// put it, instead of riding on the wrist.
///
/// Why it replaces the hand menu. The wrist menu has to be summoned before it can be read, it
/// moves while you are trying to press it, and it is on the same hand you grab things with -- on
/// hand tracking that means the menu and the grab compete for the same pinch. A panel you place
/// once and walk around stays put, stays readable, and leaves both hands free.
///
/// Why it is built in code rather than authored as a UI prefab. Everything here is state the
/// panel has to render at runtime anyway -- which gloves are connected, which scene is loaded,
/// whether passthrough is even available on this headset -- so there is no authored layout that
/// stays correct without a script driving it. Building the panel in one place keeps the layout
/// and the state that changes it in the same file, and means dropping the component on an empty
/// GameObject is the entire setup: nothing to wire in the inspector, nothing to re-wire per scene.
///
/// What it takes over from the hand menu. <see cref="HexRManager"/> drives its status text and its
/// connection and pump indicators through public fields that the old menu filled in per scene.
/// This panel assigns itself into those fields at startup, so the manager keeps driving the same
/// UI without the package changing and without five scenes needing identical inspector wiring.
/// That also settles a latent crash: <c>HexRPanel</c> was left unassigned in every scene and the
/// manager calls <c>HexRPanel.SetActive(true)</c> unguarded on a failed or dropped connection.
///
/// It survives scene loads on purpose. Switching scenes is one of the things the menu is for, so
/// it keeps its place across the load rather than reappearing in front of your face each time.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[AddComponentMenu("HexR/HexR Floating Menu")]
public class HexRFloatingMenu : MonoBehaviour
{
    /// <summary>The live panel, so anything else can call <see cref="Recenter"/>.</summary>
    public static HexRFloatingMenu Instance { get; private set; }

    [Header("Placement")]
    [Tooltip("How far in front of the head the panel is placed, in metres.")]
    public float spawnDistance = 0.65f;

    [Tooltip("How far below eye level the panel is placed, in metres. Negative is down.")]
    public float spawnHeightOffset = -0.2f;

    [Tooltip("Sideways offset from the user, in metres. Negative is to the left. Off to one side " +
             "by default: a panel directly in front is in the way of whatever you are doing, and " +
             "it is where people drag it to anyway.")]
    public float spawnSideOffset = -0.35f;

    [Tooltip("If the panel ends up further than this from the head it comes back in front of you, " +
             "so it cannot be pushed somewhere unreachable. Zero disables the recall.")]
    public float recallDistance = 4f;

    [Header("Panel")]
    [Tooltip("Panel width in metres. Height follows from the contents.")]
    public float panelWidth = 0.26f;

    [Tooltip("Put the panel in front of the user when the scene starts. Untick to leave it exactly " +
             "where it sits in the scene -- which is what the Scene view preview is showing you.")]
    public bool recenterOnStart = true;

    [Header("Behaviour")]
    [Tooltip("Switch the old wrist menu off. Untick to run both while comparing them.")]
    public bool hideHandMenu = true;

    [Tooltip("Switch the old in-world Scene Panel off. Its demo buttons are reproduced in this " +
             "panel's DEMOS section, which is built from the same GameObjects it toggled.")]
    public bool hideSceneSelector = true;

    [Tooltip("Draw the panel in the Scene view while editing, so it is not an empty GameObject " +
             "until you press Play. The preview is built and thrown away in the editor and is " +
             "never saved into the scene.")]
    public bool previewInEditor = true;

    // The panel is laid out in pixels and then scaled down to metres, which is the usual way to
    // keep a world-space canvas crisp: font sizes and paddings stay in numbers that read like UI
    // numbers instead of fractions of a millimetre.
    private const float k_PanelWidthPx = 360f;
    private const float k_Margin = 14f;
    private const float k_HeaderHeight = 46f;
    private const float k_SectionLabelHeight = 24f;
    private const float k_ButtonHeight = 44f;
    private const float k_GridButtonHeight = 34f;
    private const float k_GridGap = 5f;
    private const float k_StatusHeight = 20f;
    private const float k_Gap = 6f;
    private const float k_DotSize = 12f;

    private static readonly Color k_Background = new Color32(0x15, 0x18, 0x1E, 0xF5);
    private static readonly Color k_Header = new Color32(0x2D, 0x35, 0x42, 0xFF);
    private static readonly Color k_SectionLabel = new Color32(0x8A, 0x93, 0xA3, 0xFF);
    private static readonly Color k_ButtonNormal = new Color32(0x2B, 0x34, 0x42, 0xFF);
    private static readonly Color k_ButtonHighlight = new Color32(0x3B, 0x47, 0x59, 0xFF);
    private static readonly Color k_ButtonPressed = new Color32(0x4D, 0x5D, 0x74, 0xFF);
    private static readonly Color k_Accent = new Color32(0x1E, 0x6F, 0xEB, 0xFF);
    private static readonly Color k_Connected = new Color32(0x3D, 0xDC, 0x84, 0xFF);
    private static readonly Color k_Pump = new Color32(0xFF, 0xB0, 0x20, 0xFF);
    private static readonly Color k_Text = new Color32(0xF0, 0xF3, 0xF7, 0xFF);
    private static readonly Color k_TextDim = new Color32(0x9A, 0xA4, 0xB4, 0xFF);
    private static readonly Color k_Warn = new Color32(0xFF, 0x7A, 0x6B, 0xFF);

    private RectTransform panel;
    private Canvas canvas;
    private TextMeshProUGUI leftStatus;
    private TextMeshProUGUI rightStatus;
    private GameObject leftConnectedDot;
    private GameObject rightConnectedDot;
    private GameObject leftPumpDot;
    private GameObject rightPumpDot;
    private TextMeshProUGUI passthroughLabel;
    private TextMeshProUGUI collidersLabel;
    private IHexRPassthrough passthrough;

    // The demo groups 0.Full Demo switches between. Scene-specific, so they are re-found on every
    // load and the section disappears in the scenes that have none.
    private readonly List<GameObject> demoGroups = new List<GameObject>();
    private readonly List<Button> demoButtons = new List<Button>();
    private readonly List<Image> demoButtonImages = new List<Image>();

    private float pixelsToMetres;
    private Transform head;

    private void Awake()
    {
        if (!Application.isPlaying)
        {
            // Edit mode builds a throwaway preview instead, from OnEnable.
            return;
        }

        // Entering play mode without a scene reload leaves the editor preview standing, and it
        // would otherwise sit there as a second panel for the whole session.
        ClearPanel();

        // One panel for the session. Every scene carries a copy so it can be entered directly from
        // the editor, and the copy that arrives second stands down -- the same shape HexRManager
        // and SceneLoader use.
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        transform.SetParent(null, true);
        DontDestroyOnLoad(gameObject);

        // The interactors' physics mask is the Default layer, so the panel has to be on it to be
        // grabbable at all, whatever layer the GameObject it was dropped on happened to be.
        gameObject.layer = 0;

        Build();
        AddGrab();
    }

    private void OnEnable()
    {
        if (!Application.isPlaying)
        {
            // Edit mode only, and the preview it queues is compiled out of a player build -- so the
            // call has to be too, not just the branch that can never be taken there.
#if UNITY_EDITOR
            RequestPreviewRebuild();
#endif
            return;
        }

        SceneManager.sceneLoaded += OnSceneLoaded;
        HexRManager.ConnectAttemptEnded += OnConnectAttemptEnded;
    }

    private void OnDisable()
    {
        if (!Application.isPlaying)
        {
            ClearPanel();
            return;
        }

        SceneManager.sceneLoaded -= OnSceneLoaded;
        HexRManager.ConnectAttemptEnded -= OnConnectAttemptEnded;
    }

    private void Start()
    {
        if (!Application.isPlaying)
        {
            return;
        }

        AdoptScene();

        if (recenterOnStart)
        {
            pendingRecenter = true;
            Recenter();
        }
    }

    private void OnDestroy()
    {
        if (!Application.isPlaying)
        {
            ClearPanel();
        }

        if (Instance == this)
        {
            Instance = null;
        }
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (Application.isPlaying)
        {
            return;
        }

        RequestPreviewRebuild();
    }
#endif

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        OnSceneLoadedRebuild();
    }

    /// <summary>
    /// Re-attaches the panel to whatever the newly loaded scene supplies. Everything the panel
    /// talks to either persists across loads (the manager) or does not (the camera), and the ones
    /// that persist hold references to the ones that do not, so this runs after every load rather
    /// than once at startup.
    /// </summary>
    private void AdoptScene()
    {
        head = Camera.main != null ? Camera.main.transform : null;

        if (canvas != null)
        {
            // The canvas persists across loads; the camera it renders against does not.
            canvas.worldCamera = Camera.main;
        }

        BindToManager();
        BindPassthrough();
        RefreshDemoButtons();

        if (hideHandMenu)
        {
            HideLegacy("Hand Menu With Button Activation");
        }

        if (hideSceneSelector)
        {
            HideLegacy("Scene Panel");
        }
    }

    private void OnSceneLoadedRebuild()
    {
        // The DEMOS section is built from GameObjects that belong to the scene, so the panel is
        // rebuilt on a load rather than patched. Only the canvas is remade -- the grab rig lives on
        // this GameObject and survives, along with wherever the user put the panel.
        ClearPanel();
        Build();
        AdoptScene();
    }

    private void Update()
    {
        if (!Application.isPlaying)
        {
            return;
        }

        if (head == null)
        {
            // The camera arrives a frame or two after a load on device -- later still on Meta,
            // where the eye anchors are enabled after Start.
            head = ResolveHead();
            if (head == null)
            {
                return;
            }
        }

        if (pendingRecenter)
        {
            Recenter();
        }

        // Not while it is being held. A ray interactor can push the panel well past the recall
        // distance and still be holding it, and snapping it back out of the user's grip mid-push
        // is the one way this safety net could make things worse.
        if (Backend() != null && Backend().IsGrabbed(gameObject))
        {
            return;
        }

        if (recallDistance > 0f
            && Vector3.Distance(transform.position, head.position) > recallDistance)
        {
            Recenter();
        }
    }

    /// <summary>Puts the panel back in front of the user, facing them. Safe to call any time.</summary>
    public void Recenter()
    {
        if (head == null)
        {
            head = ResolveHead();
            if (head == null)
            {
                return;
            }
        }

        // Flattened forward, so the panel hangs level however the user's head is tilted.
        Vector3 forward = head.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = Vector3.forward;
        }

        forward.Normalize();

        // Flattened right, so the side offset stays level whatever the head is doing.
        Vector3 right = Vector3.Cross(Vector3.up, forward);

        transform.position = head.position
                           + forward * spawnDistance
                           + right * spawnSideOffset
                           + Vector3.up * spawnHeightOffset;

        // Face the user rather than simply aligning with their forward. With no side offset the
        // two are identical, so placement straight ahead is unchanged; offset to one side, this is
        // what keeps the panel readable instead of leaving it edge-on.
        Vector3 facing = transform.position - head.position;
        facing.y = 0f;
        if (facing.sqrMagnitude < 0.0001f)
        {
            facing = forward;
        }

        transform.rotation = Quaternion.LookRotation(facing.normalized, Vector3.up);
        pendingRecenter = false;
    }

    /// <summary>
    /// The user's head. Camera.main first, but not only: it returns the first *enabled* camera
    /// tagged MainCamera, and a rig that enables its eye anchors late -- or tags them differently
    /// -- would leave the panel wherever it was authored forever.
    /// </summary>
    private static Transform ResolveHead()
    {
        if (Camera.main != null)
        {
            return Camera.main.transform;
        }

        Camera any = HexRCompat.FindAny<Camera>();
        return any != null ? any.transform : null;
    }

    // ---------------------------------------------
    // Wiring
    // ---------------------------------------------

    /// <summary>
    /// Hands the manager this panel's widgets. The manager writes straight into these fields' text
    /// and active state, so they have to point at something live before the first connect button
    /// press -- several of those writes are unguarded.
    /// </summary>
    private void BindToManager()
    {
        HexRManager manager = ResolveManager();
        if (manager == null)
        {
            return;
        }

        manager.LeftBtText = leftStatus;
        manager.RightBtText = rightStatus;
        manager.BluetoothIndicatorL = leftConnectedDot;
        manager.BluetoothIndicatorR = rightConnectedDot;
        manager.pumpIndicator_L = leftPumpDot;
        manager.pumpIndicator_R = rightPumpDot;

        // The manager shows this on a failed or dropped connection so the user can retry. The
        // panel is always visible, so the call is a no-op -- the point is that it is no longer a
        // null dereference.
        manager.HexRPanel = gameObject;
    }

    /// <summary>
    /// The HexR manager, preferring its own singleton and falling back to a scene search.
    ///
    /// The fallback is not belt-and-braces. <c>HexRManager.Instance</c> is a plain static assigned
    /// in Awake, and a domain reload -- which the editor performs whenever scripts recompile,
    /// including while play mode is running -- clears every static while leaving the manager's
    /// GameObject alive in DontDestroyOnLoad. Observed here on the first play-mode test: the object
    /// was in the DontDestroyOnLoad scene, proving Awake had run, while the static read as a true
    /// null. Trusting the static alone would leave the panel reporting "no HexR rig" for the rest
    /// of the session with the rig sitting in front of it.
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

    private void BindPassthrough()
    {
        if (passthrough == null)
        {
            // An interface, so it cannot be found by type directly. Scanned once and cached;
            // the field is only null on the first press and after a scene load.
            foreach (MonoBehaviour candidate in HexRCompat.FindAll<MonoBehaviour>(true))
            {
                passthrough = candidate as IHexRPassthrough;
                if (passthrough != null) break;
            }
        }

        // It rides on the HexR rig, which survives scene loads, while the camera it dims does not.
        if (passthrough != null)
        {
            passthrough.RefreshCamera();
        }

        RefreshPassthroughLabel();
    }

    /// <summary>
    /// Switches off one of the menus this panel replaces. Done at runtime rather than by deleting
    /// them from the scenes, so the change is one tick-box to undo and the scenes keep working
    /// unchanged if this component is removed.
    /// </summary>
    private static void HideLegacy(string namePrefix)
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid())
        {
            return;
        }

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.StartsWith(namePrefix) && t.gameObject.activeSelf)
                {
                    t.gameObject.SetActive(false);
                }
            }
        }
    }

    // ---------------------------------------------
    // Actions
    // ---------------------------------------------

    /// <summary>
    /// A connect attempt finished without connecting -- it failed, was refused permission, or the
    /// glove dropped. The manager has already written the reason into the status text; this makes
    /// it visible at a glance and clears the dot, which the permission-denied path never did.
    ///
    /// Nothing latches, so nothing can get stuck: the manager raises no matching "succeeded" event,
    /// and a state that could only be cleared by a signal that may never arrive is worse than none.
    /// The next press resets the colour, and a scene load rebuilds the labels outright.
    /// </summary>
    private void OnConnectAttemptEnded(HaptGlove.HaptGloveHandler.HandType hand)
    {
        bool left = hand == HaptGlove.HaptGloveHandler.HandType.Left;

        // Both are null between ClearPanel() and Build() on a scene load.
        GameObject dot = left ? leftConnectedDot : rightConnectedDot;
        if (dot != null)
        {
            dot.SetActive(false);
        }

        TextMeshProUGUI status = left ? leftStatus : rightStatus;
        if (status != null)
        {
            status.color = k_Warn;
        }
    }

    private void ConnectLeft()
    {
        if (leftStatus != null)
        {
            leftStatus.color = k_TextDim;
        }

        HexRManager manager = ResolveManager();
        if (manager == null)
        {
            leftStatus.text = "No HexR rig in this scene";
            return;
        }

        BindToManager();
        manager.ConnectLeftBT();
    }

    private void ConnectRight()
    {
        if (rightStatus != null)
        {
            rightStatus.color = k_TextDim;
        }

        HexRManager manager = ResolveManager();
        if (manager == null)
        {
            rightStatus.text = "No HexR rig in this scene";
            return;
        }

        BindToManager();
        manager.ConnectRightBT();
    }

    private void TogglePassthrough()
    {
        if (passthrough == null)
        {
            BindPassthrough();
        }

        if (passthrough != null)
        {
            passthrough.Toggle();
        }

        RefreshPassthroughLabel();
    }

    private void RefreshPassthroughLabel()
    {
        if (passthroughLabel == null)
        {
            return;
        }

        if (passthrough == null || !passthrough.IsAvailable)
        {
            // Honest rather than broken-looking: in the editor, and on any runtime without the
            // extension, there is no camera feed to show and the button genuinely does nothing.
            passthroughLabel.text = "Passthrough  -  headset only";
            passthroughLabel.color = k_TextDim;
            return;
        }

        bool on = passthrough != null && passthrough.IsOn;
        passthroughLabel.text = on ? "Switch to VR" : "Switch to Passthrough";
        passthroughLabel.color = k_Text;
    }

    /// <summary>
    /// Draw or hide the glove's finger and palm colliders.
    ///
    /// The same control the package's HexR Panel offers, which this menu replaced -- an OpenXR
    /// project was left without it. Deliberately SetVisible rather than ColliderToggle: the button
    /// owns the state, so the label and what is actually drawn cannot drift apart the way a
    /// flip-on-each-press would if anything else touched the visualizer.
    /// </summary>
    private void ToggleColliders()
    {
        HaptGloveCollidersVisualizer visualizer = ResolveVisualizer(true);
        if (visualizer != null)
        {
            visualizer.SetVisible(!visualizer.IsShowing);
        }

        RefreshCollidersLabel();
    }

    private void RefreshCollidersLabel()
    {
        if (collidersLabel == null)
        {
            return;
        }

        HaptGloveCollidersVisualizer visualizer = ResolveVisualizer(false);
        if (visualizer == null && ResolveManager() == null)
        {
            // No rig in the scene yet, so there is nothing to draw colliders for. Say so rather
            // than offering a button that quietly does nothing.
            collidersLabel.text = "Colliders  -  no rig";
            collidersLabel.color = k_TextDim;
            return;
        }

        collidersLabel.text = visualizer != null && visualizer.IsShowing ? "Hide Colliders" : "Show Colliders";
        collidersLabel.color = k_Text;
    }

    /// <summary>
    /// The one visualizer, on the HexR Manager.
    ///
    /// It resolves both hands through the manager rather than scanning its own children, so a
    /// single instance there covers every hand root -- and it rides the manager's
    /// DontDestroyOnLoad, so the button keeps working across a scene change instead of pointing at
    /// a visualizer that went down with the old scene.
    /// </summary>
    private static HaptGloveCollidersVisualizer ResolveVisualizer(bool create)
    {
        HexRManager manager = ResolveManager();
        if (manager == null)
        {
            return null;
        }

        HaptGloveCollidersVisualizer visualizer = manager.GetComponent<HaptGloveCollidersVisualizer>();
        if (visualizer == null && create)
        {
            visualizer = manager.gameObject.AddComponent<HaptGloveCollidersVisualizer>();
        }

        return visualizer;
    }

    // ---------------------------------------------
    // Construction
    // ---------------------------------------------

    private void Build()
    {
        // Rebuilt from scratch on every scene load, and on every inspector change in edit mode,
        // so nothing may carry over.
        demoButtons.Clear();
        demoButtonImages.Clear();
        pixelsToMetres = panelWidth / k_PanelWidthPx;

        GameObject canvasGo = new GameObject("Panel", typeof(RectTransform), typeof(Canvas),
            typeof(CanvasScaler), typeof(GraphicRaycaster));
        Backend()?.MakeCanvasPointable(canvasGo.GetComponent<Canvas>());
        canvasGo.layer = 0;
        panel = (RectTransform)canvasGo.transform;
        panel.SetParent(transform, false);

        // Pivot at the top edge so the object's own origin sits on the header -- the part you
        // grab -- and the panel hangs below it. Something that rotates about the handle you are
        // holding is the difference between placing it and wrestling it.
        panel.pivot = new Vector2(0.5f, 1f);
        panel.anchorMin = new Vector2(0.5f, 0.5f);
        panel.anchorMax = new Vector2(0.5f, 0.5f);
        panel.anchoredPosition3D = Vector3.zero;
        panel.localRotation = Quaternion.identity;
        panel.localScale = Vector3.one * pixelsToMetres;

        canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;

        CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 4f;
        scaler.referencePixelsPerUnit = 100f;

        Image background = NewImage("Background", panel, k_Background);
        Stretch(background.rectTransform);

        float y = 0f;
        y = BuildHeader(y);
        y = BuildGloveSection(y);
        y = BuildViewSection(y);
        y = BuildDemoSection(y);

        float height = -y + k_Margin;
        panel.sizeDelta = new Vector2(k_PanelWidthPx, height);
    }

    private float BuildHeader(float y)
    {
        Image header = NewImage("Header", panel, k_Header);
        Place(header.rectTransform, 0f, y, k_PanelWidthPx, k_HeaderHeight);

        TextMeshProUGUI title = NewLabel("Title", header.rectTransform, "HexR", 20f,
            FontStyles.Bold, k_Text, TextAlignmentOptions.Left);
        Stretch(title.rectTransform, k_Margin, 0f);

        TextMeshProUGUI hint = NewLabel("Hint", header.rectTransform, "grab here to move", 12f,
            FontStyles.Normal, k_TextDim, TextAlignmentOptions.Right);
        Stretch(hint.rectTransform, k_Margin, 0f);

        return y - k_HeaderHeight - k_Gap;
    }

    private float BuildGloveSection(float y)
    {
        y = SectionLabel("GLOVES", y);

        y = GloveRow(y, "Connect Left Glove", ConnectLeft, out leftStatus, out leftConnectedDot, out leftPumpDot);
        y = GloveRow(y, "Connect Right Glove", ConnectRight, out rightStatus, out rightConnectedDot, out rightPumpDot);

        return y;
    }

    private float GloveRow(float y, string label, UnityEngine.Events.UnityAction onClick,
        out TextMeshProUGUI status, out GameObject connectedDot, out GameObject pumpDot)
    {
        NewButton(label, y, k_ButtonHeight, k_ButtonNormal, onClick, out _);
        y -= k_ButtonHeight + 2f;

        // Two dots at the right of the status line, driven by HexRManager: connection on the
        // outside, pump next to it. They start off, which is the honest state before a connect.
        float dotY = y - (k_StatusHeight - k_DotSize) * 0.5f;
        connectedDot = NewDot("Connected", k_PanelWidthPx - k_Margin - k_DotSize, dotY, k_Connected);
        pumpDot = NewDot("Pump", k_PanelWidthPx - k_Margin - k_DotSize * 2f - 5f, dotY, k_Pump);

        status = NewLabel("Status", panel, "Not connected", 13f, FontStyles.Normal, k_TextDim,
            TextAlignmentOptions.Left);
        Place(status.rectTransform, k_Margin, y, k_PanelWidthPx - k_Margin * 2f - k_DotSize * 2f - 10f,
            k_StatusHeight);

        return y - k_StatusHeight - k_Gap;
    }

    private float BuildViewSection(float y)
    {
        y = SectionLabel("VIEW", y);

        NewButton("Switch to Passthrough", y, k_ButtonHeight, k_ButtonNormal, TogglePassthrough,
            out passthroughLabel);
        RefreshPassthroughLabel();
        y -= k_ButtonHeight + 2f;

        NewButton("Show Colliders", y, k_ButtonHeight, k_ButtonNormal, ToggleColliders,
            out collidersLabel);
        RefreshCollidersLabel();

        return y - k_ButtonHeight - k_Gap;
    }

    /// <summary>
    /// The demo switcher that used to be the in-world Scene Panel: one button per
    /// "[name] Demo Components" group in the scene, exactly one of them on at a time.
    ///
    /// Found by name rather than wired in the inspector, which is the same bargain the rest of this
    /// component makes. The four groups in 0.Full Demo already follow that convention exactly, and
    /// the old panel's buttons did nothing but SetActive on them. A scene with no such groups gets
    /// no DEMOS section at all, which is every scene but that one.
    /// </summary>
    private float BuildDemoSection(float y)
    {
        demoGroups.Clear();
        FindDemoGroups(demoGroups);

        if (demoGroups.Count == 0)
        {
            return y;
        }

        y = SectionLabel("DEMOS", y);

        for (int i = 0; i < demoGroups.Count; i++)
        {
            GameObject group = demoGroups[i];
            float x, cellY, width;
            GridCell(i, y, out x, out cellY, out width);

            Button button = NewButtonAt(DemoDisplayName(group.name), x, cellY, width,
                k_GridButtonHeight, k_ButtonNormal, 12f, () => ShowDemo(group), out _);

            demoButtons.Add(button);
            demoButtonImages.Add(button.GetComponent<Image>());
        }

        return y - GridHeight(demoGroups.Count) - k_Gap;
    }

    private static void FindDemoGroups(List<GameObject> into)
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid())
        {
            return;
        }

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.EndsWith(" Demo Components"))
                {
                    into.Add(t.gameObject);
                }
            }
        }
    }

    /// <summary>"Grab Demo Components" reads as "Grab Demo" on a button.</summary>
    private static string DemoDisplayName(string groupName)
    {
        const string suffix = " Components";
        return groupName.EndsWith(suffix)
            ? groupName.Substring(0, groupName.Length - suffix.Length)
            : groupName;
    }

    private void ShowDemo(GameObject group)
    {
        for (int i = 0; i < demoGroups.Count; i++)
        {
            if (demoGroups[i] != null)
            {
                demoGroups[i].SetActive(demoGroups[i] == group);
            }
        }

        RefreshDemoButtons();
    }

    private void RefreshDemoButtons()
    {
        for (int i = 0; i < demoButtons.Count && i < demoGroups.Count; i++)
        {
            bool on = demoGroups[i] != null && demoGroups[i].activeSelf;
            demoButtonImages[i].color = on ? k_Accent : k_ButtonNormal;
            demoButtons[i].interactable = !on;
        }
    }

    /// <summary>Position of one cell in the two-column grid the list sections use.</summary>
    private static void GridCell(int index, float sectionTop, out float x, out float y, out float width)
    {
        width = (k_PanelWidthPx - k_Margin * 2f - k_GridGap) * 0.5f;
        x = k_Margin + (index % 2) * (width + k_GridGap);
        y = sectionTop - (index / 2) * (k_GridButtonHeight + k_GridGap);
    }

    private static float GridHeight(int count)
    {
        int rows = (count + 1) / 2;
        return rows * (k_GridButtonHeight + k_GridGap);
    }

    /// <summary>
    /// Makes the panel grabbable by its header only. A collider over the whole panel would swallow
    /// every button press, because a hand reaching for a button would grab the panel first.
    /// </summary>
    private void AddGrab()
    {
        Rigidbody body = gameObject.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        body.interpolation = RigidbodyInterpolation.None;

        BoxCollider box = gameObject.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, -k_HeaderHeight * 0.5f * pixelsToMetres, 0f);
        box.size = new Vector3(k_PanelWidthPx * pixelsToMetres, k_HeaderHeight * pixelsToMetres, 0.02f);

        // Which interactable that means is the backend's business -- XRGrabInteractable on
        // OpenXR, Grabbable plus HandGrabInteractable on Meta. The Rigidbody and collider above
        // are the same either way, so they stay here.
        Backend()?.MakeGrabbable(gameObject);
    }

    /// <summary>
    /// Whichever interaction SDK this rig runs on, or null in a project with neither.
    /// </summary>
    private IHexRInteractionBackend Backend()
    {
        // Cached: this is asked every frame from the recall check, and Resolve walks the scene
        // for a HexR Manager when the registry cannot answer from the rig alone.
        if (cachedBackend == null)
        {
            string diagnostic;
            cachedBackend = HexRInteractionBackends.Resolve(HexRBackendChoice.Auto, gameObject, out diagnostic);
        }

        return cachedBackend;
    }

    private IHexRInteractionBackend cachedBackend;

    // Set when a recentre was asked for but the camera was not up yet. Without this the
    // panel sits wherever it was authored -- on a rig-parented menu, that is the floor --
    // because the only other caller of Recenter is the recall check, which needs the user to
    // walk recallDistance away before it fires.
    private bool pendingRecenter;

    // ---------------------------------------------
    // Scene view preview
    // ---------------------------------------------

    /// <summary>
    /// Tears down the built panel, wherever it came from. Matching by name rather than by the
    /// cached field on purpose: a domain reload wipes the field but not the objects, so the field
    /// alone would leak a panel every recompile.
    /// </summary>
    private void ClearPanel()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform child = transform.GetChild(i);
            if (child != null && child.name == "Panel")
            {
                if (Application.isPlaying)
                {
                    Destroy(child.gameObject);
                }
                else
                {
                    DestroyImmediate(child.gameObject);
                }
            }
        }

        panel = null;
        canvas = null;
        leftStatus = null;
        rightStatus = null;
        leftConnectedDot = null;
        rightConnectedDot = null;
        leftPumpDot = null;
        rightPumpDot = null;
        passthroughLabel = null;
        collidersLabel = null;
        demoGroups.Clear();
        demoButtons.Clear();
        demoButtonImages.Clear();
    }

#if UNITY_EDITOR
    private bool previewRebuildQueued;

    /// <summary>
    /// Queues a preview rebuild for the next editor tick. Both callers -- OnEnable and OnValidate
    /// -- are points where Unity forbids creating or destroying GameObjects, so the work cannot
    /// happen inline.
    /// </summary>
    private void RequestPreviewRebuild()
    {
        if (previewRebuildQueued)
        {
            return;
        }

        previewRebuildQueued = true;
        UnityEditor.EditorApplication.delayCall += RebuildPreview;
    }

    private void RebuildPreview()
    {
        previewRebuildQueued = false;

        // The component can be deleted, or play mode entered, between the queue and the tick.
        if (this == null || Application.isPlaying)
        {
            return;
        }

        // Building and tearing down GameObjects marks the scene modified even when none of them
        // are saved, which would leave the scene permanently asking to be saved for a preview that
        // changes nothing on disk. Only a scene that was already clean gets its flag put back.
        Scene scene = gameObject.scene;
        bool wasClean = scene.IsValid() && !scene.isDirty;

        ClearPanel();

        if (previewInEditor && isActiveAndEnabled)
        {
            Build();
            HidePreview();
        }

        if (wasClean && scene.IsValid())
        {
            ClearSceneDirtiness(scene);
        }
    }

    /// <summary>
    /// Puts a scene's "no unsaved changes" flag back.
    ///
    /// Unity marks a scene modified whenever its hierarchy changes, including for objects flagged
    /// DontSave that will never reach the file. Without this the Scene view preview would leave
    /// every scene permanently asking to be saved over a change that does not exist. There is no
    /// public API for it, so this goes through the internal one and quietly does nothing if a
    /// future version moves it -- a stale dirty flag is a far smaller problem than an exception.
    /// </summary>
    private static void ClearSceneDirtiness(Scene scene)
    {
        System.Reflection.MethodInfo clear = typeof(UnityEditor.SceneManagement.EditorSceneManager)
            .GetMethod("ClearSceneDirtiness",
                System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic);

        if (clear != null)
        {
            clear.Invoke(null, new object[] { scene });
        }
    }

    /// <summary>
    /// Keeps the preview out of the Hierarchy and, more importantly, out of the saved scene: what
    /// the Scene view shows is the real panel, while the scene file still holds nothing but this
    /// one empty GameObject.
    /// </summary>
    private void HidePreview()
    {
        if (panel == null)
        {
            return;
        }

        foreach (Transform t in panel.GetComponentsInChildren<Transform>(true))
        {
            t.gameObject.hideFlags = HideFlags.HideAndDontSave;
        }
    }
#endif

    // ---------------------------------------------
    // Small UI builders
    // ---------------------------------------------

    private float SectionLabel(string text, float y)
    {
        TextMeshProUGUI label = NewLabel(text, panel, text, 12f, FontStyles.Bold, k_SectionLabel,
            TextAlignmentOptions.Left);
        label.characterSpacing = 8f;
        Place(label.rectTransform, k_Margin, y, k_PanelWidthPx - k_Margin * 2f, k_SectionLabelHeight);

        return y - k_SectionLabelHeight;
    }

    private Button NewButton(string text, float y, float height, Color color,
        UnityEngine.Events.UnityAction onClick, out TextMeshProUGUI label)
    {
        return NewButtonAt(text, k_Margin, y, k_PanelWidthPx - k_Margin * 2f, height, color, 15f,
            onClick, out label);
    }

    private Button NewButtonAt(string text, float x, float y, float width, float height, Color color,
        float fontSize, UnityEngine.Events.UnityAction onClick, out TextMeshProUGUI label)
    {
        Image image = NewImage(text, panel, color);
        Place(image.rectTransform, x, y, width, height);

        Button button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.ColorTint;

        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = Multiply(k_ButtonHighlight, color);
        colors.pressedColor = Multiply(k_ButtonPressed, color);
        colors.selectedColor = Color.white;
        colors.disabledColor = new Color(1f, 1f, 1f, 0.6f);
        colors.fadeDuration = 0.05f;
        button.colors = colors;

        button.onClick.AddListener(onClick);

        label = NewLabel("Label", image.rectTransform, text, fontSize, FontStyles.Normal, k_Text,
            TextAlignmentOptions.Center);

        // The grid columns are narrower than the longest scene name, so a label that does not fit
        // shrinks instead of being clipped to something unreadable.
        label.enableAutoSizing = true;
        label.fontSizeMin = 8f;
        label.fontSizeMax = fontSize;
        Stretch(label.rectTransform, 8f, 0f);

        return button;
    }

    /// <summary>
    /// Button tinting multiplies the target graphic's own colour, so the highlight and pressed
    /// states have to be expressed relative to it rather than as flat colours.
    /// </summary>
    private static Color Multiply(Color wanted, Color baseColor)
    {
        return new Color(
            baseColor.r > 0.001f ? wanted.r / baseColor.r : 1f,
            baseColor.g > 0.001f ? wanted.g / baseColor.g : 1f,
            baseColor.b > 0.001f ? wanted.b / baseColor.b : 1f,
            1f);
    }

    private GameObject NewDot(string name, float x, float y, Color color)
    {
        Image dot = NewImage(name, panel, color);
        Place(dot.rectTransform, x, y, k_DotSize, k_DotSize);
        dot.gameObject.SetActive(false);
        return dot.gameObject;
    }

    private static Image NewImage(string name, Transform parent, Color color)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.layer = 0;
        go.transform.SetParent(parent, false);

        Image image = go.GetComponent<Image>();
        image.color = color;
        // No sprite: a plain tinted quad, which is all any of these are, and it saves every one of
        // them depending on an imported sprite that has to exist in the project.
        image.sprite = null;
        return image;
    }

    private static TextMeshProUGUI NewLabel(string name, Transform parent, string text, float size,
        FontStyles style, Color color, TextAlignmentOptions alignment)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.layer = 0;
        go.transform.SetParent(parent, false);

        // Font left unassigned on purpose: TMP fills in TMP_Settings.defaultFontAsset itself, which
        // is whatever the project's TextMesh Pro resources supply. Naming a font here would tie the
        // panel to one asset path.
        TextMeshProUGUI label = go.AddComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = size;
        label.fontStyle = style;
        label.color = color;
        label.alignment = alignment;
        label.raycastTarget = false;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Ellipsis;
        return label;
    }

    /// <summary>Places a rect by its top-left corner, in panel pixels, y running negative downward.</summary>
    private static void Place(RectTransform rt, float x, float y, float width, float height)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(width, height);
        rt.anchoredPosition = new Vector2(x, y);
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
    }

    private static void Stretch(RectTransform rt, float padX = 0f, float padY = 0f)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(padX, padY);
        rt.offsetMax = new Vector2(-padX, -padY);
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
    }
}
