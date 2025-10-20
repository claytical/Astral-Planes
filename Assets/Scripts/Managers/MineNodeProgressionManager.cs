using System.Linq;
using System;
using System.Collections.Generic;
using Gameplay.Mining;
using UnityEngine;
[Serializable]
public struct PhaseStrategyBinding {
    public MusicalPhase phase;
    public SpawnStrategyProfile strategy;
}

public class MineNodeProgressionManager : MonoBehaviour
{
    
    [Header("Tracking")]
    public MusicalPhaseQueue phaseQueue;
    private int currentPhaseIndex;

    [Header("Utility bias per phase (0=spawner only, 1=utility only)")]
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
    [SerializeField] private CosmicDustGenerator dustGen;
    [SerializeField] private DrumTrack drum;
    private CosmicDustGenerator _hookedGen;
    private int _phaseIndexCursor = 0;
    private void Awake()
    {
        Debug.Log($"[MPM] MazeReady: ptmPhase={GameFlowManager.Instance?.phaseTransitionManager?.currentPhase}");

        if (!drum) drum = GameFlowManager.Instance?.activeDrumTrack; // whatever your getter is
        if (!dustGen) dustGen = GameFlowManager.Instance?.dustGenerator;
    }
    private void EnsureMazeHook()
    {
        var gen = GameFlowManager.Instance?.dustGenerator;
        if (gen == null) return;

        if (_hookedGen == gen) return;             // already hooked to this instance
        if (_hookedGen != null) _hookedGen.OnMazeReady -= HandleMazeReady;

        gen.OnMazeReady += HandleMazeReady;
        _hookedGen = gen;

        Debug.Log($"[MPM] Subscribed to OnMazeReady on gen#{gen.GetInstanceID()}");
    }

    void OnEnable()  { EnsureMazeHook(); }
    void OnDisable() { if (_hookedGen) _hookedGen.OnMazeReady -= HandleMazeReady; _hookedGen = null; }
    void Start()
    {
        Debug.Log($"[MPM] MazeReady: ptmPhase={GameFlowManager.Instance?.phaseTransitionManager?.currentPhase}");
        if (!drum)   drum   = GameFlowManager.Instance?.activeDrumTrack ?? FindObjectOfType<DrumTrack>();
        if (!dustGen && GameFlowManager.Instance?.dustGenerator != null)
        {
            dustGen = GameFlowManager.Instance.dustGenerator;
        }
    }
    private void HandleMazeReady(Vector2Int? cellHint)
    {
        Debug.Log("[MPM] Maze is ready, requesting PhaseStar");
        var gfm = GameFlowManager.Instance;
        var ptm = gfm ? gfm.phaseTransitionManager : null;
        if (drum == null) drum = gfm?.activeDrumTrack ?? FindObjectOfType<DrumTrack>();
        Debug.Log($"[MPM] MazeReady: ptmPhase={(ptm ? ptm.currentPhase.ToString() : "<null>")} cell={ (cellHint.HasValue ? cellHint.Value.ToString() : "<none>")}");
        if (ptm == null) { Debug.LogError("[MPM] PTM Null or Drum at maze ready."); return; }
        if (drum == null) { Debug.LogError("[MPM] DrumTrack null at MazeReady"); return; }
        var phase = ptm.currentPhase;               // 👈 after fix #1, this is the new phase
        Debug.Log($"[MPM] Requesting star for {phase}");
        drum.RequestPhaseStar(phase, cellHint);
    }
    public void BootFirstPhaseStar(MusicalPhase startPhase = MusicalPhase.Establish, bool regenerateMaze = true)
    {
        EnsureMazeHook();
        drum = GameFlowManager.Instance?.activeDrumTrack ?? FindFirstObjectByType<DrumTrack>();
        var ptm = GameFlowManager.Instance?.phaseTransitionManager;
        if (ptm && ptm.currentPhase != startPhase)
            ptm.HandlePhaseTransition(startPhase, "Boot");
        
        if (regenerateMaze && dustGen != null)
        {
            // 🔒 Guaranteed alive: this component
            StartCoroutine(dustGen.GenerateMazeThenPlacePhaseStar(startPhase));
        }
        else
        {
            drum.RequestPhaseStar(startPhase, null);
        }
    }

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

    public float GetHollowRadiusForCurrentPhase(MusicalPhase phase)
    {
        var reg = GameFlowManager.Instance?.activeDrumTrack.phasePersonalityRegistry;
        if (reg != null)
        {
            var persona = reg.Get(phase);
            return Mathf.Max(0f, persona.starHoleRadius);
        }

        return 0f;
    }

    public int GetCurrentPhaseIndex()
    {
        return currentPhaseIndex;
    }
    
}