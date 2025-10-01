using System.Linq;
using System;
using System.Collections.Generic;
using UnityEngine;

public class MineNodeProgressionManager : MonoBehaviour
{
    private DrumTrack _drumTrack;
    private InstrumentTrackController _trackController;

    [Header("Progression Settings")]
    public List<MusicalPhaseProfile> phaseProfiles;
    
    [Header("Tracking")]
    public MusicalPhaseQueue phaseQueue;
    private int currentPhaseIndex;
    SpawnStrategyProfile currentSpawnStrategy;
    SpawnStrategyProfile overrideSpawnStrategy;

    public bool isPhaseInProgress;
    public bool isPhaseTransitioning;
    [HideInInspector] public bool pendingNextPhase = false;
    private bool hasStartedFirstPhase = false;
    private MusicalPhase currentPhase;
    public bool phaseStarActive = false; // 🛡️ Blocks progression until PhaseStar completes
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
    
    private void Awake()
    {
        _drumTrack = GetComponent<DrumTrack>();
        _trackController = GetComponent<InstrumentTrackController>();
        // 🌟 Register globally
        MusicalPhaseLibrary.InitializeProfiles(phaseProfiles);
        if (!_drumTrack || !_trackController)
            Debug.LogError("MineNodeProgressionManager is missing required components on the same GameObject.");
    }
    public void BeginMazeTransitionForNextPhase(float approxSeconds)
    {
        if (_drumTrack?.hexMazeGenerator == null) return;

        // N.B. Your RunLoopAlignedMazeCycle already takes phase + center + durations.
        // Use current phase as a visual reset before spawning the next star.
        Vector2Int centerCell = _drumTrack.WorldToGridPosition(_drumTrack.transform.position);
        float loopSeconds = _drumTrack.GetLoopLengthInSeconds();
        float growPct = 0.25f;
        float holdPct = 0.50f;

        _drumTrack.hexMazeGenerator.StartCoroutine(
            _drumTrack.hexMazeGenerator.RunLoopAlignedMazeCycle(
                currentPhase,
                centerCell,
                loopSeconds,
                growPct,
                holdPct
            )
        );
    }
// MineNodeProgressionManager.cs
    public void BeginBridgeToNextPhase(MusicalPhase from, MusicalPhase to, List<InstrumentTrack> perfectTracks, Color nextStarColor)
    {
        var g = GameFlowManager.Instance;
        if (!g) { Debug.LogWarning("GameFlowManager missing; spawning star immediately"); SpawnNextPhaseStarWithoutLoopChange(); return; }
        Debug.Log($"Begin Phase Bridge {from} to {to} with {perfectTracks.Count} tracks using {nextStarColor} for next color");
        g.BeginPhaseBridge(from, to, perfectTracks, nextStarColor);
    }
    public MusicalPhase GetNextPhase() => PeekNextPhase();
// MineNodeProgressionManager.cs
    public Color ResolveNextPhaseStarColor(MusicalPhase phase)
    {
        Color c = _drumTrack.phasePersonalityRegistry.Get(phase).starColor;
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
    public void SpawnNextPhaseStarWithoutLoopChange()
    {
        var next = PeekNextPhase();
        Debug.Log($"Current Phase: {currentPhase}, Next Phase: {next}");
        currentPhase = next;
        
        if (_drumTrack != null) _drumTrack.currentPhase = currentPhase;

        var group = GetPhaseGroup(currentPhase);
        var profile = SelectSpawnStrategy(group);
        if (profile == null) { Debug.LogError($"[Progression] No strategy for {currentPhase}"); return; }

        if (_drumTrack != null && _drumTrack.hexMazeGenerator != null)
        {
            // Build+place star with the proper timing instead of direct spawn:
            _drumTrack.StartCoroutine(_drumTrack.hexMazeGenerator.GenerateMazeThenPlacePhaseStar(currentPhase, profile));
        }
        else
        {
            // Fallback: direct spawn (previous behavior)
            _drumTrack?.SpawnPhaseStar(currentPhase, profile);
        }
    }

    public void MoveToNextPhase(
        MusicalPhase? specificPhase = null,
        Func<MusicalPhaseGroup, bool> filter = null
    )
    {
        overrideSpawnStrategy = null;

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
        currentPhase = selectedGroup.phase;
        _drumTrack.currentPhase = currentPhase;
        Debug.Log($"Moving to phase: {currentPhase}");

        if (selectedGroup.allowRandomSelection)
        {
            currentSpawnStrategy = selectedGroup.spawnStrategies[
                UnityEngine.Random.Range(0, selectedGroup.spawnStrategies.Count)
            ];
        }
        else
        {
            currentSpawnStrategy = selectedGroup.spawnStrategies[0];
        }
        // Arm the loop for the phase we just selected — commit on FIRST POKE of the new star
        GameFlowManager.Instance.ArmNextPhaseLoop(currentPhase);

        _drumTrack.SpawnPhaseStar(currentPhase, currentSpawnStrategy);

    }
    public string GetCurrentPhaseName()
    {
        var grp = phaseQueue.phaseGroups[GetCurrentPhaseIndex()];
        return grp.phase.ToString(); // e.g., "Evolve", "Wildcard"
    }
    public float GetHollowRadiusForCurrentPhase()
    {
        var group = phaseQueue.phaseGroups[currentPhaseIndex];
        return group.hollowRadius;
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

    private MusicalPhase PeekNextPhase()
    {
        var current = phaseQueue.phaseGroups[currentPhaseIndex].phase;

        switch (current)
        {
            case MusicalPhase.Establish: return MusicalPhase.Evolve;
            case MusicalPhase.Evolve: return MusicalPhase.Intensify;
            case MusicalPhase.Intensify: return MusicalPhase.Release;
            case MusicalPhase.Release: return MusicalPhase.Wildcard;
            case MusicalPhase.Wildcard: return MusicalPhase.Pop;
            case MusicalPhase.Pop: return MusicalPhase.Evolve;
            default: return MusicalPhase.Establish;
        }
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

    public SpawnStrategyProfile SelectSpawnStrategy(MusicalPhaseGroup group)
    {
        if (group == null || group.spawnStrategies == null || group.spawnStrategies.Count == 0) return null;
        if (group.allowRandomSelection)
            return group.spawnStrategies[UnityEngine.Random.Range(0, group.spawnStrategies.Count)];
        return group.spawnStrategies[0];
    }

    public SpawnStrategyProfile GetCurrentSpawnerStrategyProfile()
    {
        return overrideSpawnStrategy ?? phaseQueue?.phaseGroups[currentPhaseIndex].spawnStrategies.FirstOrDefault();
    }
    
    public int GetCurrentPhaseIndex()
    {
        return currentPhaseIndex;
    }
    
    
}