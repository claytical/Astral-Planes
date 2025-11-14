using System;
using System.Collections.Generic;
using System.Linq;
using Gameplay.Mining;
using UnityEngine;

[Serializable]
public struct PhaseStrategyBinding
{
    public MusicalPhase phase;
    public SpawnStrategyProfile strategy;
}

public class MineNodeProgressionManager : MonoBehaviour
{
    [Header("Phase Queue / Strategy")]
    public MusicalPhaseQueue phaseQueue;
    [SerializeField] private List<PhaseStrategyBinding> phaseStrategies = new();

    // Index is only used for analytics / debugging now; *not* as phase truth.
    [SerializeField] private int currentPhaseIndex;

    [Header("Utility bias per phase (0 = spawner only, 1 = utility only)")]
    public float establishUtilityBias = 0.10f;
    public float evolveUtilityBias    = 0.20f;
    public float intensifyUtilityBias = 0.30f;
    public float releaseUtilityBias   = 0.45f;
    public float wildcardUtilityBias  = 0.60f;
    public float popUtilityBias       = 0.70f;

    [Header("Ring drop multiplier by phase (optional)")]
    public float establishRingMul = 1.0f;
    public float evolveRingMul    = 1.1f;
    public float intensifyRingMul = 1.2f;
    public float releaseRingMul   = 1.3f;
    public float wildcardRingMul  = 1.4f;
    public float popRingMul       = 1.5f;

    [Header("Optional thresholds")]
    public int minNodesToAdvance = 3;

    [Header("Scene References")]
    [SerializeField] private CosmicDustGenerator dustGenerator;
    [SerializeField] private DrumTrack drum;

    // Bridge flags used by GameFlowManager after PlayPhaseBridge
    [NonSerialized] public bool isPhaseInProgress;
    [NonSerialized] public bool isPhaseTransitioning;
    [NonSerialized] public bool pendingNextPhase;

    private CosmicDustGenerator _hookedGen;
    private MusicalPhase currentPhase;
    private DrumTrack drumTrack;

    private void Awake()
    {
        // Resolve references from GameFlowManager if not explicitly wired
        var gfm = GameFlowManager.Instance;
        if (!drum)    drum    = gfm != null ? gfm.activeDrumTrack   : FindFirstObjectByType<DrumTrack>();
        if (!dustGenerator) dustGenerator = gfm != null ? gfm.dustGenerator     : FindFirstObjectByType<CosmicDustGenerator>();

        Debug.Log($"[MPM] Awake. PTM phase = {gfm?.phaseTransitionManager?.currentPhase}");
    }

    private void OnEnable()
    {
        EnsureMazeHook();
    }

    private void OnDisable()
    {
        if (_hookedGen != null)
        {
            _hookedGen.OnMazeReady -= HandleMazeReady;
            _hookedGen = null;
        }
    }

    private void Start()
    {
        Debug.Log("[MPM] Start");
        EnsureMazeHook();
    }
    private void EnsureMazeHook()
    {
        var gfm = GameFlowManager.Instance;
        var gen = (gfm != null) ? gfm.dustGenerator : dustGenerator;

        if (gen == null)
            return;

        if (_hookedGen == gen)
            return; // already hooked

        if (_hookedGen != null)
            _hookedGen.OnMazeReady -= HandleMazeReady;

        gen.OnMazeReady += HandleMazeReady;
        _hookedGen = gen;

        Debug.Log($"[MPM] Subscribed to OnMazeReady on gen #{gen.GetInstanceID()}");
    }


    private void UpdatePhaseIndexFromQueue(MusicalPhase phase)
    {
        if (phaseQueue?.phaseGroups == null || phaseQueue.phaseGroups.Count == 0)
            return;

        int idx = phaseQueue.phaseGroups.FindIndex(g => g.phase == phase);
        if (idx < 0) idx = 0;

        currentPhaseIndex = idx;
    }

    /// <summary>
    /// Used by GameFlowManager’s bridge logic to choose a SpawnStrategyProfile for the given phase.
    /// </summary>
    public SpawnStrategyProfile SelectSpawnStrategy(MusicalPhase phase)
    {
        if (phaseStrategies == null || phaseStrategies.Count == 0)
            return null;

        for (int i = 0; i < phaseStrategies.Count; i++)
        {
            if (phaseStrategies[i].phase == phase)
                return phaseStrategies[i].strategy;
        }

        return null;
    }

    /// <summary>
    /// Fallback used by GameFlowManager if the maze/star pipeline fails.
    /// </summary>
    public void SpawnNextPhaseStarWithoutLoopChange()
    {
        var gfm = GameFlowManager.Instance;
        if (gfm == null || drum == null)
        {
            Debug.LogWarning("[MPM] SpawnNextPhaseStarWithoutLoopChange: missing references.");
            return;
        }

        var ptm = gfm.phaseTransitionManager;
        var phase = (ptm != null) ? ptm.currentPhase : MusicalPhase.Establish;

        Debug.Log($"[MPM] Fallback PhaseStar spawn for phase {phase} (no maze).");
        drum.RequestPhaseStar(phase, null);
    }

    /// <summary>
    /// Compute the next phase based on the MusicalPhaseQueue and the *current* PTM phase.
    /// Used by PhaseTransitionManager / GameFlowManager when arming the next phase.
    /// </summary>
    public MusicalPhase ComputeNextPhase()
    {
        var ptm = GameFlowManager.Instance?.phaseTransitionManager;
        var cur = (ptm != null) ? ptm.currentPhase : MusicalPhase.Establish;
        return NextInQueueAfter(cur);
    }

    private MusicalPhase NextInQueueAfter(MusicalPhase current)
    {
        if (phaseQueue?.phaseGroups == null || phaseQueue.phaseGroups.Count == 0)
            return MusicalPhase.Establish;

        int idx = phaseQueue.phaseGroups.FindIndex(g => g.phase == current);
        if (idx < 0) idx = 0;

        int nextIdx = (idx + 1) % phaseQueue.phaseGroups.Count;
        return phaseQueue.phaseGroups[nextIdx].phase;
    }

    /// <summary>
    /// Reads star hole radius from the DrumTrack’s PhasePersonalityRegistry for the given phase.
    /// </summary>
    public float GetHollowRadiusForCurrentPhase(MusicalPhase phase)
    {
        var reg = GameFlowManager.Instance?.activeDrumTrack?.phasePersonalityRegistry;
        if (reg != null)
        {
            var persona = reg.Get(phase);
            return Mathf.Max(0f, persona.starHoleRadius);
        }

        return 0f;
    }

private void HandleMazeReady(Vector2Int? cell)
{
    var gfm  = GameFlowManager.Instance;
    if (gfm == null)
    {
        Debug.LogWarning("[MPM] HandleMazeReady: GameFlowManager not found.");
        return;
    }

    var drum = gfm.activeDrumTrack;
    if (drum == null)
    {
        Debug.LogWarning("[MPM] HandleMazeReady: DrumTrack not found, cannot spawn PhaseStar.");
        return;
    }

    var dustGen = gfm.dustGenerator ?? dustGenerator;

    // Figure out which phase we’re in for the star request.
    var ptm = gfm.phaseTransitionManager;
    var phase = ptm != null ? ptm.currentPhase : MusicalPhase.Establish;

    // Decide which cell to use:
    Vector2Int selectedCell;
    if (cell.HasValue)
    {
        selectedCell = cell.Value;
    }
    else
    {
        selectedCell = Vector2Int.zero;
    }

    Debug.Log($"[MPM] HandleMazeReady: maze ready, requesting PhaseStar for phase={phase} at cell={selectedCell}");

    // NEW: we no longer use an onComplete callback or BeginPhaseStarAtDefaultCell.
    // DrumTrack takes a cell hint and handles off-screen entry + landing.
    drum.RequestPhaseStar(phase, selectedCell);
}

public void BootFirstPhaseStar(MusicalPhase phase, bool regenerateMaze)
{
    var gfm = GameFlowManager.Instance;
    if (gfm == null)
    {
        Debug.LogWarning("[MPM] BootFirstPhaseStar: GameFlowManager not found.");
        return;
    }

    var dustGen = gfm.dustGenerator ?? dustGenerator;
    if (dustGen == null)
    {
        Debug.LogWarning("[MPM] BootFirstPhaseStar: CosmicDustGenerator not found.");
        return;
    }

    // Make sure we’re actually listening to OnMazeReady on the current generator.
    EnsureMazeHook();

    Debug.Log($"[MPM] BootFirstPhaseStar: phase={phase}, regenerateMaze={regenerateMaze}");

    // DESIGN GOAL:
    // - Grow maze first (staggered)
    // - When growth finishes, CosmicDustGenerator fires OnMazeReady(cell)
    // - HandleMazeReady() then tells DrumTrack to RequestPhaseStar at that cell.
    dustGen.GenerateMazeThenPlacePhaseStar(phase);
}

    private void EnsureDustHook()
    {
        if (dustGenerator == null)
            dustGenerator = FindObjectOfType<CosmicDustGenerator>();

        if (dustGenerator == null) return;

        if (_hookedGen == dustGenerator) return;

        if (_hookedGen != null)
            _hookedGen.OnMazeReady -= HandleMazeReady;

        dustGenerator.OnMazeReady += HandleMazeReady;
        _hookedGen = dustGenerator;
    }

    public int GetCurrentPhaseIndex()
    {
        return currentPhaseIndex;
    }
}
