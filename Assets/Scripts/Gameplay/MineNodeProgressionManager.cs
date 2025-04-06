using System.Linq;
using System;
using UnityEngine;

public class MineNodeProgressionManager : MonoBehaviour
{
    private DrumTrack drumTrack;
    private InstrumentTrackController trackController;

    [Header("Progression Settings")]
    public int currentSetIndex = 0;
    public int requiredNotesPerTrack = 6;
    public int minimumLoopCount = 0;
    public bool requireLoopCount = false;
    [Header("Tracking")]
    public int totalMinedObjectsCollected;
    public int loopsCompleted;
    public SpawnerPhaseQueue phaseQueue;
    private int currentPhaseIndex = 0;
    private MineNodeSpawnerSet currentSpawnerSet;
    private MineNodeSpawnerSet overrideSet;
    private void Awake()
    {
        drumTrack = GetComponent<DrumTrack>();
        trackController = GetComponent<InstrumentTrackController>();

        if (!drumTrack || !trackController)
            Debug.LogError("MineNodeProgressionManager is missing required components on the same GameObject.");
    }
    public void MoveToNextPhase(
        SpawnerPhase? specificPhase = null,
        Func<SpawnerPhaseGroup, bool> filter = null
    )
    {
        drumTrack.ClearAllActiveMineNodes();
        SpawnerPhaseGroup selectedGroup = null;

        // Option 1: Go to a specific phase
        if (specificPhase.HasValue)
        {
            selectedGroup = phaseQueue.phaseGroups
                .FirstOrDefault(g => g.phase == specificPhase.Value);

            if (selectedGroup == null)
            {
                Debug.LogWarning($"🚫 No phase group found for phase '{specificPhase.Value}'");
                return;
            }
        }
        // Option 2: Use a filter to choose from valid groups
        else if (filter != null)
        {
            var candidates = phaseQueue.phaseGroups.Where(filter).ToList();
            if (candidates.Count == 0)
            {
                Debug.LogWarning("🚫 No phase groups matched the provided filter.");
                return;
            }

            selectedGroup = candidates[UnityEngine.Random.Range(0, candidates.Count)];
        }
        else
        {
            Debug.LogWarning("⚠️ MoveToNextPhase called with no specificPhase or filter.");
            return;
        }

        currentPhaseIndex = phaseQueue.phaseGroups.IndexOf(selectedGroup);

        if (selectedGroup.spawnerOptions.Count == 0)
        {
            Debug.LogWarning($"⚠️ Phase '{selectedGroup.phase}' has no spawner options.");
            return;
        }

        if (selectedGroup.allowRandomSelection)
        {
            currentSpawnerSet = selectedGroup.spawnerOptions[
                UnityEngine.Random.Range(0, selectedGroup.spawnerOptions.Count)
            ];
        }
        else
        {
            currentSpawnerSet = selectedGroup.spawnerOptions[0];
        }

        drumTrack.SetMineNodeSpawnerSet(currentSpawnerSet);
        drumTrack.SetPatternFromPhase(selectedGroup.phase);

        Debug.Log($"🎶 Phase advanced to: {selectedGroup.phase} → Set: {currentSpawnerSet.name}");
    }
    
    public void NotifyStarsCollected()
    {
        // Force progression evaluation when stars are merged
        EvaluateProgression();
        
        // This ensures that we immediately perform the transition rather than 
        // waiting for the next loop check
        var currentPhase = GetCurrentPhase();
        
        // Define the next phase based on the current one
        SpawnerPhase nextPhase;
        switch (currentPhase)
        {
            case SpawnerPhase.Establish:
                nextPhase = SpawnerPhase.Evolve;
                break;
            case SpawnerPhase.Evolve:
                nextPhase = SpawnerPhase.Intensify;
                break;
            case SpawnerPhase.Intensify:
                nextPhase = SpawnerPhase.Release;
                break;
            case SpawnerPhase.Release:
                // Here you can decide if you want to go to Evolve or WildCard
                nextPhase = UnityEngine.Random.Range(0, 2) == 0 ? SpawnerPhase.Evolve : SpawnerPhase.WildCard;
                break;
            case SpawnerPhase.WildCard:
                nextPhase = SpawnerPhase.Pop;
                break;
            case SpawnerPhase.Pop:
                nextPhase = SpawnerPhase.Evolve;
                break;
            default:
                nextPhase = SpawnerPhase.Establish;
                break;
        }
        
        // Force the move to the next phase
        MoveToNextPhase(specificPhase: nextPhase);
        
        Debug.Log($"[MineNodeProgressionManager] Stars collected. Transitioning from {currentPhase} to {nextPhase}");
    }
    private void SetSpawnerSetFromGroup(SpawnerPhaseGroup group)
    {
        if (group.allowRandomSelection && group.spawnerOptions.Count > 0)
        {
            currentSpawnerSet = group.spawnerOptions[
                UnityEngine.Random.Range(0, group.spawnerOptions.Count)
            ];
        }
        else if (group.spawnerOptions.Count > 0)
        {
            currentSpawnerSet = group.spawnerOptions[0];
        }
        else
        {
            Debug.LogWarning($"⚠️ Phase group {group.phase} has no spawner options.");
            return;
        }

        drumTrack.SetMineNodeSpawnerSet(currentSpawnerSet);
    }

    public void EvaluateProgression()
    {
        var current = phaseQueue.phaseGroups[currentPhaseIndex].phase;

        int totalNotes = GetTotalNoteCount();
        int denseTracks = drumTrack.trackController.tracks.Count(t => t.GetNoteDensity() >= 6);
        int sparseTracks = drumTrack.trackController.tracks.Count(t => t.GetNoteDensity() <= 2);
        int starsCollected = drumTrack.GetCollectedStarCount();
Debug.Log($"StarsCollected: {starsCollected}, Total Notes :{totalNotes} Dense Tracks :{denseTracks}, sparse Tracks :{sparseTracks}");
        switch (current)
        {
            case SpawnerPhase.Establish:
                if (starsCollected >= 4)
                {
                    // Move to variation or instrument expression
                    MoveToNextPhase(specificPhase: SpawnerPhase.Evolve);
                }
                break;

            case SpawnerPhase.Evolve:
                if (starsCollected >= 4)
                {
                    // Tension building phase
                    MoveToNextPhase(specificPhase: SpawnerPhase.Intensify);
                }
                break;

            case SpawnerPhase.Intensify:
                if (starsCollected >= 4)
                {
                    // Trigger loop switch / groove drop
                    MoveToNextPhase(specificPhase: SpawnerPhase.Release);
                }
                break;

            case SpawnerPhase.Release:
                if (starsCollected >= 4)
                {
                    // Return to evolve or introduce some weird variation
                    MoveToNextPhase(filter: g =>
                        g.phase == SpawnerPhase.Evolve || g.phase == SpawnerPhase.WildCard
                    );
                }
                break;

            case SpawnerPhase.WildCard:
                if (starsCollected >= 4)
                {
                    // Bring it back into control
                    MoveToNextPhase(specificPhase: SpawnerPhase.Pop);
                }
                break;
            case SpawnerPhase.Pop:
                if (starsCollected == 4)
                {
                    MoveToNextPhase(specificPhase: SpawnerPhase.Evolve);
                }
                break;
        }
    }


    public MineNodeSpawnerSet GetCurrentSpawnerSet()
    {
        return overrideSet ?? phaseQueue?.phaseGroups[currentPhaseIndex].spawnerOptions.FirstOrDefault();
    }

    public void OverrideSpawnerSet(MineNodeSpawnerSet set)
    {
        overrideSet = set;
    }
    public void SetPhase(SpawnerPhase targetPhase)
    {
        var group = phaseQueue.phaseGroups.FirstOrDefault(g => g.phase == targetPhase);
        if (group == null)
        {
            Debug.LogWarning($"Phase {targetPhase} not found in queue.");
            return;
        }

        currentPhaseIndex = phaseQueue.phaseGroups.IndexOf(group);
        SpawnerPhaseGroup selectedGroup = phaseQueue.phaseGroups[currentPhaseIndex];

        if (selectedGroup.allowRandomSelection)
        {
            currentSpawnerSet = selectedGroup.spawnerOptions[
                UnityEngine.Random.Range(0, selectedGroup.spawnerOptions.Count)
            ];
        }
        else if (selectedGroup.spawnerOptions.Count > 0)
        {
            currentSpawnerSet = selectedGroup.spawnerOptions[0];
        }

        Debug.Log($"🎛 Manually switched to phase {targetPhase} → Set: {currentSpawnerSet.name}");
        drumTrack.SetMineNodeSpawnerSet(currentSpawnerSet);
    }

    public int GetTotalNoteCount()
    {
        if (drumTrack.trackController == null) return 0;

        int total = 0;
        foreach (var track in drumTrack.trackController.tracks)
        {
            total += track.GetNoteDensity();
        }
        return total;
    }

    public InstrumentTrack GetDensestTrack()
    {
        InstrumentTrack densest = null;
        int maxNotes = 0;

        foreach (var track in drumTrack.trackController.tracks)
        {
            int count = track.GetNoteDensity();
            if (count > maxNotes)
            {
                maxNotes = count;
                densest = track;
            }
        }
        return densest;
    }
    public SpawnerPhase GetCurrentPhase()
    {
        if (phaseQueue == null || phaseQueue.phaseGroups.Count == 0)
        {
            Debug.LogWarning("[MineNodeProgressionManager] No phase queue defined.");
            return SpawnerPhase.Establish; // Fallback
        }

        if (currentPhaseIndex < 0 || currentPhaseIndex >= phaseQueue.phaseGroups.Count)
        {
            Debug.LogWarning("[MineNodeProgressionManager] Invalid currentPhaseIndex.");
            return SpawnerPhase.Establish;
        }

        return phaseQueue.phaseGroups[currentPhaseIndex].phase;
    }

    public void OnMinedObjectCollected()
    {
        totalMinedObjectsCollected++;
        EvaluateProgression();
    }
    public int GetCurrentPhaseIndex()
    {
        return currentPhaseIndex;
    }


    public void OnLoopCompleted()
    {
        loopsCompleted++;
    }

    public int GetCurrentSetIndex()
    {
        return currentSetIndex;
    }

    public bool ShouldAdvanceMineNodeSet()
    {
        bool allTracksHaveEnoughNotes = trackController.tracks.All(t => t.CollectedNotesCount >= requiredNotesPerTrack);
        bool loopRequirementMet = !requireLoopCount || loopsCompleted >= minimumLoopCount;

        return allTracksHaveEnoughNotes && loopRequirementMet;
    }

    public void TryAdvanceSet()
    {
        if (ShouldAdvanceMineNodeSet())
        {
            currentSetIndex++;
            Debug.Log($"🎵 Advanced to mine node set {currentSetIndex}!");
        }
    }
}