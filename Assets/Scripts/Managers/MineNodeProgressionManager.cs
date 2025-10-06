using System.Linq;
using System;
using System.Collections.Generic;
using UnityEngine;
[Serializable]
public struct PhaseStrategyBinding {
    public MusicalPhase phase;
    public SpawnStrategyProfile strategy;
}

public class MineNodeProgressionManager : MonoBehaviour
{
    [Header("Strategy Authority")]
    public List<PhaseStrategyBinding> strategyOverrides;
    public SpawnStrategyProfile _globalProfile;
    
    private DrumTrack _drumTrack;
    private InstrumentTrackController _trackController;

    [Header("Progression Settings")]
    public List<MusicalPhaseProfile> phaseProfiles;
    
    [Header("Tracking")]
    public MusicalPhaseQueue phaseQueue;
    private int currentPhaseIndex;
    public bool isPhaseInProgress;
    public bool isPhaseTransitioning;
    [HideInInspector] public bool pendingNextPhase = false;
    private bool _hasStartedFirstPhase, _spawnInFlight;
    public bool phaseStarActive = false; // 🛡️ Blocks progression until PhaseStar completes

    public bool IsSpawnInFlight => _spawnInFlight;
    // === Perfect-loop hold gate ===
    private bool remixArmed = false;
    private MusicalPhaseProfile pendingPhaseProfileOnCommit = null;

// Arm when a star finishes perfectly (all notes ultimately collected)
    public void ArmRemixHold()
    {
        remixArmed = true;
        pendingPhaseProfileOnCommit = null;
    }

    public void BindPendingPhase(MusicalPhaseProfile profile)
    {
        if (remixArmed) pendingPhaseProfileOnCommit = profile;
    }
    // MineNodeProgressionManager (or wherever you choose a strategy)


    private void Awake()
    {
        _drumTrack = GetComponent<DrumTrack>();
        _trackController = GetComponent<InstrumentTrackController>();
        // 🌟 Register globally
        MusicalPhaseLibrary.InitializeProfiles(phaseProfiles);
        if (!_drumTrack || !_trackController)
            Debug.LogError("MineNodeProgressionManager is missing required components on the same GameObject.");
    }

    public void BeginBridgeToNextPhase(MusicalPhase from, MusicalPhase to, List<InstrumentTrack> perfectTracks, Color nextStarColor)
    {
        var g = GameFlowManager.Instance;
        if (!g) { Debug.LogWarning("GameFlowManager missing; spawning star immediately"); SpawnNextPhaseStarWithoutLoopChange(); return; }
        Debug.Log($"Begin Phase Bridge {from} to {to} with {perfectTracks.Count} tracks using {nextStarColor} for next color");
        g.BeginPhaseBridge(from, to, perfectTracks, nextStarColor);
    }
    // === ONE authority for "what's next?" ===
    public MusicalPhase ComputeNextPhase()
    {
        // Use PTM.currentPhase as the source of truth
        var ptm = GameFlowManager.Instance?.phaseTransitionManager;
        var cur = (ptm != null) ? ptm.currentPhase : MusicalPhase.Establish;
        return NextInQueueAfter(cur);
    }

// If you want hardcoded mapping, keep it here (not scattered).
// Otherwise, queue order is fine:
    private MusicalPhase NextInQueueAfter(MusicalPhase current)
    {
        if (phaseQueue?.phaseGroups == null || phaseQueue.phaseGroups.Count == 0)
            return MusicalPhase.Establish;

        int idx = phaseQueue.phaseGroups.FindIndex(g => g.phase == current);
        if (idx < 0) idx = 0;
        int nextIdx = (idx + 1) % phaseQueue.phaseGroups.Count;
        return phaseQueue.phaseGroups[nextIdx].phase;
    }
    //public MusicalPhase GetNextPhase() => PeekNextPhase();
    public Color ResolveNextPhaseStarColor(MusicalPhase phase)
    {
        Color c = GameFlowManager.Instance.activeDrumTrack.phasePersonalityRegistry.Get(phase).starColor;
        if (c != Color.white)
        {
            return c;
        }
        // Or: derive from your current spawn strategy/phase visuals if available

        // Fallback: reuse the current star color (safe but not ideal),
        // or borrow a deterministic track color so the bridge coral matches something stable.
        var ctrl = GetComponent<InstrumentTrackController>();
        if (ctrl != null && ctrl.tracks != null && ctrl.tracks.Length > 0 && ctrl.tracks[0] != null)
            return ctrl.tracks[0].trackColor;

        return Color.white;
    }

    public void NotifyPhaseStarActivated(MusicalPhase phaseFromStar)
    {
        pendingNextPhase     = false;
        isPhaseTransitioning = false;
        isPhaseInProgress    = true;
        phaseStarActive      = true;
        _spawnInFlight       = false;
    }


// Mirrors PhaseTransitionManager's phase, used only for local sequencing.
    private int _phaseIndexCursor = 0;

    private int GetPhaseIndex(MusicalPhase phase)
    {
        if (phaseQueue?.phaseGroups == null || phaseQueue.phaseGroups.Count == 0) return 0;
        var idx = phaseQueue.phaseGroups.FindIndex(g => g.phase == phase);
        return Mathf.Max(0, idx);
    }

    private MusicalPhase PhaseAt(int index)
    {
        if (phaseQueue?.phaseGroups == null || phaseQueue.phaseGroups.Count == 0) return MusicalPhase.Establish;
        var i = Mathf.Clamp(index, 0, phaseQueue.phaseGroups.Count - 1);
        return phaseQueue.phaseGroups[i].phase;
    }
    private void SyncCursorFromPTM()
    {
        var ptm = GameFlowManager.Instance?.phaseTransitionManager;
        if (ptm == null) return;
        _phaseIndexCursor = GetPhaseIndex(ptm.currentPhase); // expose a CurrentPhase property on PTM if not already
    }

// MineNodeProgressionManager.cs
    public void SpawnNextPhaseStarWithoutLoopChange(MusicalPhase phase, bool setPTM)
    {
        if (_spawnInFlight) { Debug.Log("[Progression] Spawn in flight; skip."); return; }
        if (phaseStarActive || (_drumTrack != null && _drumTrack.isPhaseStarActive))
        {
            Debug.Log("[Progression] PhaseStar already active; skip spawn.");
            return;
        }
        _spawnInFlight = true;

        var ptm = GameFlowManager.Instance?.phaseTransitionManager;

        // Only advance PTM if we are the authority in this call path
        if (setPTM && ptm != null && ptm.currentPhase != phase)
            ptm.HandlePhaseTransition(phase, "MPM/SpawnNextPhaseStar");

        var profile = SelectSpawnStrategy(phase);
        if (profile == null)
        {
            Debug.LogError($"[Progression] No spawn strategy for {phase}");
            _spawnInFlight = false;
            return;
        }

        // Build/Place star for the explicit phase
        if (_drumTrack != null && _drumTrack.hexMazeGenerator != null)
            _drumTrack.StartCoroutine(_drumTrack.hexMazeGenerator.GenerateMazeThenPlacePhaseStar(phase, profile));
        else
            _drumTrack?.SpawnPhaseStar(phase, profile);

        // PhaseStar.Initialize MUST call back to clear these:
        //   progressionManager.NotifyPhaseStarActivated(phase);
        //   -> sets phaseStarActive = true and _spawnInFlight = false
    }

// Backwards-compatible wrapper (this is the “we decide next” path)
    public void SpawnNextPhaseStarWithoutLoopChange()
    {
        var next = ComputeNextPhase();                 // PTM-based “what’s next?”
        SpawnNextPhaseStarWithoutLoopChange(next, true);
    }

 /*
    private MusicalPhase PeekNextPhaseUsingPTM()
    {
        var ptm = GameFlowManager.Instance?.phaseTransitionManager;
        if (phaseQueue?.phaseGroups == null || phaseQueue.phaseGroups.Count == 0 || ptm == null)
            return MusicalPhase.Establish;

        int idx = phaseQueue.phaseGroups.FindIndex(g => g.phase == ptm.currentPhase);
        if (idx < 0) idx = 0; // defensive

        // reuse your existing mapping if you want,
        // or just step forward in your designed sequence:
        var current = phaseQueue.phaseGroups[idx].phase;

        switch (current) // matches your current mapping
        {
            case MusicalPhase.Establish: return MusicalPhase.Evolve;
            case MusicalPhase.Evolve:    return MusicalPhase.Intensify;
            case MusicalPhase.Intensify: return MusicalPhase.Release;
            case MusicalPhase.Release:   return MusicalPhase.Wildcard;
            case MusicalPhase.Wildcard:  return MusicalPhase.Pop;
            case MusicalPhase.Pop:       return MusicalPhase.Evolve;
            default:                     return MusicalPhase.Establish;
        }
    }
*/
    public void MoveToNextPhase(
        MusicalPhase? specificPhase = null,
        Func<MusicalPhaseGroup, bool> filter = null
    )
    {

        if (isPhaseInProgress || isPhaseTransitioning || pendingNextPhase)
            return;

        pendingNextPhase = true;
        isPhaseInProgress = true;
        isPhaseTransitioning = false;

        _drumTrack.ClearAllActiveMinedObjects();
        MusicalPhaseGroup selectedGroup = null;

        if (specificPhase.HasValue)
        {
            selectedGroup = phaseQueue.phaseGroups
                .FirstOrDefault(g => g.phase == specificPhase.Value);
        }
        else if (filter != null)
        {
            var candidates = phaseQueue.phaseGroups.Where(filter).ToList();
            selectedGroup = candidates[UnityEngine.Random.Range(0, candidates.Count)];
        }

        if (selectedGroup == null)
        {
            Debug.LogWarning("⚠️ No phase group found.");
            return;
        }

        currentPhaseIndex = phaseQueue.phaseGroups.IndexOf(selectedGroup);
        GameFlowManager.Instance.phaseTransitionManager.HandlePhaseTransition(selectedGroup.phase);
        Debug.Log($"Moving to phase: {selectedGroup.phase}");
        _globalProfile.ResetForNewPhase();
        // Arm the loop for the phase we just selected — commit on FIRST POKE of the new star
        GameFlowManager.Instance.ArmNextPhaseLoop(selectedGroup.phase);
    }
    public string GetCurrentPhaseName()
    {
        var grp = phaseQueue.phaseGroups[GetCurrentPhaseIndex()];
        return grp.phase.ToString(); // e.g., "Evolve", "Wildcard"
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
    
    public MusicalPhaseProfile GetProfileForPhase(MusicalPhase? phase)
    {
        foreach (var profile in phaseProfiles)
        {
            if (profile != null && profile.phase == phase)
                return profile;
        }

        Debug.LogWarning($"No profile found for phase: {phase}");
        return null;
    }
    public MusicalPhase PeekNextPhase()
    {
        if (phaseQueue?.phaseGroups == null || phaseQueue.phaseGroups.Count == 0) return MusicalPhase.Establish;
        var nextIdx = (_phaseIndexCursor + 1) % phaseQueue.phaseGroups.Count;
        return PhaseAt(nextIdx);
    }


    public void EvaluateProgression()
    {
        if (phaseStarActive)
        {
            return;
        }
        var current = phaseQueue.phaseGroups[currentPhaseIndex].phase;
        switch (current)
        {
            case MusicalPhase.Establish:
                MoveToNextPhase(specificPhase: MusicalPhase.Evolve);

                break;
            case MusicalPhase.Evolve:
                // Tension building phase
                MoveToNextPhase(specificPhase: MusicalPhase.Intensify);
                break;
            case MusicalPhase.Intensify:
                 // Trigger loop switch / groove drop
                MoveToNextPhase(specificPhase: MusicalPhase.Release);
                break;
            case MusicalPhase.Release:
                    // Return to evolve or introduce some weird variation
                    MoveToNextPhase(filter: g =>
                        g.phase == MusicalPhase.Evolve || g.phase == MusicalPhase.Wildcard);
                break;
            case MusicalPhase.Wildcard:
                // Bring it back into control
                MoveToNextPhase(specificPhase: MusicalPhase.Pop);
                break;
            case MusicalPhase.Pop:
                MoveToNextPhase(specificPhase: MusicalPhase.Evolve);
                break;
        }
    }
    public MusicalPhaseGroup GetPhaseGroup(MusicalPhase phase)
    {
        if (phaseQueue == null || phaseQueue.phaseGroups == null) return null;
        for (int i = 0; i < phaseQueue.phaseGroups.Count; i++)
            if (phaseQueue.phaseGroups[i].phase == phase) return phaseQueue.phaseGroups[i];
        return null;
    }

    public SpawnStrategyProfile SelectSpawnStrategy(MusicalPhase phase) {
        // Per-phase override takes precedence
        if (strategyOverrides != null) {
            for (int i = 0; i < strategyOverrides.Count; i++)
                if (strategyOverrides[i].phase == phase && strategyOverrides[i].strategy != null)
                    return strategyOverrides[i].strategy;
        }
        // Fallback to global
        if (_globalProfile != null) return _globalProfile;

        Debug.LogError($"[Progression] No SpawnStrategyProfile for phase={phase} and no _globalProfile.");
        return null;
    }

    public SpawnStrategyProfile GetCurrentSpawnerStrategyProfile()
    {
        return _globalProfile;
    }
    
    public int GetCurrentPhaseIndex()
    {
        return currentPhaseIndex;
    }
    
    
}