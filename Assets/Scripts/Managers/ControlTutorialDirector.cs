using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

public class ControlTutorialDirector : MonoBehaviour
{
    public static ControlTutorialDirector Instance { get; private set; }

    private enum PrimaryMode
    {
        Hidden,
        PressAnyAuto,
        JoinSouth,
        TutorialSequenceTimed,
        TutorialSequenceInputGated
    }

    [Header("Primary (global)")]
    [Tooltip("Prefab that contains a ControlTutorialHighlight (or has one in children). Re-instantiated per scene.")]
    [SerializeField] private GameObject primaryPrefab;

    [Tooltip("Name of a scene object used as the parent for the primary tutorial UI (place one in each scene).")]
    [SerializeField] private string primaryAnchorName = "PrimaryTutorialAnchor";

    [Header("Primary behavior")]
    [SerializeField, Min(0.1f)] private float pressAnyStepSeconds = 1.0f;
    [SerializeField] private bool pressAnyLoop = true;

    [SerializeField, Min(0.1f)] private float tutorialStepSeconds = 1.2f; // Drift -> Boost -> Charge timing
    [SerializeField] private bool useInputGatedTutorial = true;

    [SerializeField] private Vector3 primaryFullScale    = Vector3.one;
    [SerializeField] private Vector3 primaryCompactScale = new Vector3(0.5f, 0.5f, 1f);

    // The current scene instance of the primary UI (destroyed on scene load, re-instantiated).
    private ControlTutorialHighlight primaryInstance;

    // The persistent desired state (re-applied after each scene load / re-instantiation).
    private PrimaryMode _desiredMode = PrimaryMode.Hidden;
    private bool _primaryTutorialRunning;
    public bool IsPrimaryTutorialRunning => _primaryTutorialRunning;

    // Mini instances per LocalPlayer (these live inside player UI, not global).
    private readonly Dictionary<LocalPlayer, ControlTutorialHighlight> _mini = new();
    private List<LocalPlayer> _tutorialPlayers = new();

    private void Awake()
    {
        if (Instance && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDestroy()
    {
        if (Instance == this)
            SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void Update()
    {
        if (!_primaryTutorialRunning || !primaryInstance || _tutorialPlayers.Count == 0) return;

        Vector2 bestStick  = Vector2.zero;
        float   bestThrust = 0f;
        bool    anyButton  = false;

        foreach (var p in _tutorialPlayers)
        {
            if (!p) continue;
            Vector2 s = p.GetMoveInput();
            if (s.magnitude > bestStick.magnitude) bestStick = s;
            bestThrust = Mathf.Max(bestThrust, p.GetThrustInput());
            anyButton |= p.GetChooseInput();
        }

        primaryInstance.SetLiveInput(bestStick, bestThrust, anyButton);
    }

    private void OnSceneLoaded(Scene s, LoadSceneMode mode)
    {
        // New scene => the old primary instance is gone (scene unload). Rebuild it from prefab + anchor.
        RebuildPrimaryInstanceForScene();

        // Decide desired mode by scene name (edit freely)
        switch (s.name)
        {
            case "Main":
                _primaryTutorialRunning = false;
                _desiredMode = PrimaryMode.PressAnyAuto;
                break;

            case "TrackSelection":
                _primaryTutorialRunning = false;
                _desiredMode = PrimaryMode.JoinSouth;
                break;

            case "GeneratedTrack":
                _primaryTutorialRunning = false;
                _desiredMode = PrimaryMode.Hidden;
                break;

            case "TrackFinished":
                _primaryTutorialRunning = false;
                _desiredMode = PrimaryMode.PressAnyAuto;
                break;

            default:
                _primaryTutorialRunning = false;
                _desiredMode = PrimaryMode.Hidden;
                break;
        }

        ApplyDesiredPrimaryMode();
    }

    // ======================================================================
    // Primary lifecycle
    // ======================================================================
    private void RebuildPrimaryInstanceForScene()
    {
        // Destroy any prior instance (should already be destroyed by scene unload, but be safe)
        if (primaryInstance != null)
        {
            try { Destroy(primaryInstance.gameObject); } catch { }
            primaryInstance = null;
        }

        if (!primaryPrefab)
        {
            Debug.LogWarning("[CTD] No primaryPrefab assigned.");
            return;
        }

        var anchor = FindAnchorInScene(primaryAnchorName);
        if (!anchor)
        {
            Debug.LogWarning($"[CTD] No anchor '{primaryAnchorName}' found in scene '{SceneManager.GetActiveScene().name}'. Primary UI will not spawn.");
            return;
        }

        var go = Instantiate(primaryPrefab, anchor);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
//        go.transform.localScale = Vector3.one;

        primaryInstance = go.GetComponentInChildren<ControlTutorialHighlight>(true);
        if (!primaryInstance)
        {
            Debug.LogWarning("[CTD] primaryPrefab does not contain ControlTutorialHighlight (in self or children).");
            return;
        }

        // Ensure callback wired once per instance
        primaryInstance.OnTutorialFinished -= HandlePrimaryTutorialFinished;
        primaryInstance.OnTutorialFinished += HandlePrimaryTutorialFinished;
    }

    private Transform FindAnchorInScene(string anchorName)
    {
        if (string.IsNullOrEmpty(anchorName)) return null;

        // include inactive; only scene objects (not assets)
        var trs = Resources.FindObjectsOfTypeAll<Transform>();
        for (int i = 0; i < trs.Length; i++)
        {
            var t = trs[i];
            if (!t) continue;
            if (!t.gameObject.scene.IsValid()) continue;
            if (t.name == anchorName) return t;
        }
        return null;
    }

    private void ApplyDesiredPrimaryMode()
    {
        if (!primaryInstance) return;

        // Never assume active; enforce it here.
        primaryInstance.gameObject.SetActive(true);

        primaryInstance.transform.localScale =
            (_desiredMode == PrimaryMode.TutorialSequenceTimed ||
             _desiredMode == PrimaryMode.TutorialSequenceInputGated ||
             _desiredMode == PrimaryMode.JoinSouth)
                ? primaryFullScale
                : primaryCompactScale;

        switch (_desiredMode)
        {
            case PrimaryMode.Hidden:
                primaryInstance.HideAndClear(immediate: true);
                break;

            case PrimaryMode.PressAnyAuto:
                primaryInstance.SetVisible(true);
                primaryInstance.BeginPressAnyButtonGuideAuto(
                    immediateFirst: true,
                    text: "",
                    stepSeconds: pressAnyStepSeconds,
                    loop: pressAnyLoop
                );
                break;

            case PrimaryMode.JoinSouth:
                primaryInstance.SetVisible(true);
                primaryInstance.ShowWaitingFor(
                    ControlTutorialHighlight.ButtonId.East,
                    immediate: true,
                    overrideText: "press"
                );
                break;

            case PrimaryMode.TutorialSequenceTimed:
                primaryInstance.SetVisible(true);
                _primaryTutorialRunning = true;
                primaryInstance.StartTimedTutorial(stepSeconds: tutorialStepSeconds, immediateFirst: true);
                break;

            case PrimaryMode.TutorialSequenceInputGated:
                primaryInstance.SetVisible(true);
                _primaryTutorialRunning = true;
                primaryInstance.StartInputGatedTutorial(BuildInputGatedPredicates(), immediateFirst: true);
                break;
        }
    }

    private Func<bool>[] BuildInputGatedPredicates()
    {
        var sequence = primaryInstance ? primaryInstance.TutorialSequence : null;
        if (sequence == null || sequence.Length == 0) return Array.Empty<Func<bool>>();

        var players = _tutorialPlayers;
        var predicates = new Func<bool>[sequence.Length];
        for (int i = 0; i < sequence.Length; i++)
        {
            var instr = sequence[i];
            predicates[i] = instr switch
            {
                ControlTutorialHighlight.Instruction.Drift =>
                    () => players.Any(p => p && p.GetMoveInput().magnitude > 0.35f),
                ControlTutorialHighlight.Instruction.Boost =>
                    () => players.Any(p => p && p.GetMoveInput().magnitude > 0.35f && p.GetThrustInput() > 0.35f),
                ControlTutorialHighlight.Instruction.Release =>
                    () => players.Any(p => p && p.GetChooseInput()),
                _ => () => true
            };
        }
        return predicates;
    }

    // ======================================================================
    // External API: send messages to primary / minis
    // ======================================================================
    public ControlTutorialHighlight GetPrimary() => primaryInstance;

    public ControlTutorialHighlight GetMini(LocalPlayer lp)
    {
        if (!lp) return null;
        _mini.TryGetValue(lp, out var m);
        return m;
    }

    public void SendToPrimary(Action<ControlTutorialHighlight> fn)
    {
        if (primaryInstance && fn != null) fn(primaryInstance);
    }

    public void SendToMini(LocalPlayer lp, Action<ControlTutorialHighlight> fn)
    {
        var m = GetMini(lp);
        if (m && fn != null) fn(m);
    }

    public void SendToAllMinis(Action<ControlTutorialHighlight> fn)
    {
        if (fn == null) return;
        foreach (var kv in _mini)
        {
            if (kv.Value) fn(kv.Value);
        }
    }

    // ======================================================================
    // Minis
    // ======================================================================
    /// Registers a mini highlight that already exists (typically instantiated by LocalPlayer),
    /// with optional re-parenting + local pose so it lines up in PlayerSelectShip UI.
    public void RegisterMini(
        LocalPlayer lp,
        ControlTutorialHighlight mini,
        Transform parentOverride = null,
        Vector3? localPos = null,
        Vector3? localScale = null,
        Quaternion? localRot = null)
    {
        if (!lp || !mini) return;

        _mini[lp] = mini;

        // If caller provided a parent override, re-parent now (for precise UI alignment).
        if (parentOverride)
        {
            mini.transform.SetParent(parentOverride, worldPositionStays: false);
            mini.transform.localPosition = localPos ?? Vector3.zero;
            mini.transform.localRotation = localRot ?? Quaternion.identity;
            mini.transform.localScale    = localScale ?? Vector3.one;
        }

        // TrackSelection behavior: once any player joins, hide primary (global).
        if (primaryInstance) primaryInstance.HideAndClear(immediate: true);

        mini.gameObject.SetActive(true);
        mini.ShowWaitingFor(ControlTutorialHighlight.ButtonId.Arrows, immediate: true, overrideText: "choose");
    }

    public void Mini_SetConfirmStage(LocalPlayer lp)
    {
        var mini = GetMini(lp);
        if (!mini) return;

        mini.ShowWaitingFor(ControlTutorialHighlight.ButtonId.South, immediate: true, overrideText: "confirm");
    }

    public void Mini_Clear(LocalPlayer lp)
    {
        var mini = GetMini(lp);
        if (!mini) return;
        mini.Clear(immediate: true, hideText: true);
    }

    // ======================================================================
    // Primary tutorial gating
    // ======================================================================
    /// Called when all players confirmed in TrackSelection.
    public void BeginPrimaryTutorialSequence()
    {
        SendToAllMinis(m => m.Clear(immediate: true, hideText: true));
        _tutorialPlayers = new List<LocalPlayer>(_mini.Keys);

        _desiredMode = useInputGatedTutorial
            ? PrimaryMode.TutorialSequenceInputGated
            : PrimaryMode.TutorialSequenceTimed;

        _primaryTutorialRunning = true;
        ApplyDesiredPrimaryMode();
    }

    /// Compatibility method: if something still calls this, it will attempt to step manually.
    /// With timed tutorial, this is normally unnecessary, but it won’t break anything.
    /// Called by input to skip the current step's wait and advance immediately.
    public void SkipCurrentTutorialStep()
    {
        if (!_primaryTutorialRunning || !primaryInstance) return;
        primaryInstance.SkipCurrentTimedStep();
    }
    
    public void HidePrimary()
    {
        _desiredMode = PrimaryMode.Hidden;
        _primaryTutorialRunning = false;
        ApplyDesiredPrimaryMode();
    }

    private void HandlePrimaryTutorialFinished()
    {
        _primaryTutorialRunning = false;

        // Hide primary and proceed
        if (primaryInstance) primaryInstance.HideAndClear(immediate: true);

        if (GameFlowManager.Instance != null)
            GameFlowManager.Instance.BeginGameAfterTutorial();
    }
}