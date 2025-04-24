using System.Linq;
using System;
using System.Collections.Generic;
using UnityEngine;

public class MineNodeProgressionManager : MonoBehaviour
{
    private DrumTrack drumTrack;
    private InstrumentTrackController trackController;

    [Header("Progression Settings")]
    public int currentSetIndex ;
    public int requiredNotesPerTrack = 6;
    public int minimumLoopCount;
    public bool requireLoopCount;
    public List<MusicalPhaseProfile> phaseProfiles;

    [Header("Tracking")]
    public int totalMinedObjectsCollected;
    public int loopsCompleted;
    public MusicalPhaseQueue phaseQueue;
    private int currentPhaseIndex;
    private MineNodeSpawnerSet currentSpawnerSet;
    private MineNodeSpawnerSet overrideSet;
    public bool phaseLocked;
    public bool isPhaseInProgress;
    public bool isPhaseTransitioning;
    [HideInInspector] public bool pendingNextPhase = false;
    private bool hasStartedFirstPhase = false;

    private void Awake()
    {
        drumTrack = GetComponent<DrumTrack>();
        trackController = GetComponent<InstrumentTrackController>();

        if (!drumTrack || !trackController)
            Debug.LogError("MineNodeProgressionManager is missing required components on the same GameObject.");
    }
    public void MoveToNextPhase(
        MusicalPhase? specificPhase = null,
        Func<MusicalPhaseGroup, bool> filter = null
    )
    {
        if (isPhaseInProgress || isPhaseTransitioning || pendingNextPhase)
        {
            return;
        }

        pendingNextPhase = true;

        isPhaseInProgress = true;
        isPhaseTransitioning = false;
        drumTrack.ClearAllActiveMineNodes();
        MusicalPhaseGroup selectedGroup = null;

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

            selectedGroup = candidates[(int)UnityEngine.Random.Range((float)0, candidates.Count)];
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


        drumTrack.BeginPhase(selectedGroup.phase, currentSpawnerSet);
        
        phaseLocked = false;
        isPhaseInProgress = true;
        isPhaseTransitioning = false;
        pendingNextPhase = false;

    }
    

    public void BeginFirstPhase()
    {
        if (hasStartedFirstPhase) return;

        if (phaseQueue == null || phaseQueue.phaseGroups.Count == 0)
        {
            Debug.LogError("No phases defined in phaseQueue!");
            return;
        }

        var firstGroup = phaseQueue.phaseGroups[0];
        var spawnerSet = firstGroup.spawnerOptions[0];

        drumTrack.BeginPhase(firstGroup.phase, spawnerSet);
        hasStartedFirstPhase = true;
        isPhaseInProgress = true;
    }
    public MusicalPhaseProfile GetProfileForPhase(MusicalPhase phase)
    {
        foreach (var profile in phaseProfiles)
        {
            if (profile != null && profile.phase == phase)
                return profile;
        }

        Debug.LogWarning($"No profile found for phase: {phase}");
        return null;
    }

    public MusicalPhase? PeekNextPhase()
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
            default: return null;
        }
    }

    public void NotifyStarsCollected()
    {
        if (phaseLocked || isPhaseTransitioning) return;
        phaseLocked = true;

        EvaluateProgression();
        
    }

    private void EvaluateProgression()
    {
        var current = phaseQueue.phaseGroups[currentPhaseIndex].phase;

//        int totalNotes = GetTotalNoteCount();
        int denseTracks = drumTrack.trackController.tracks.Count(t => t.GetNoteDensity() >= 6);
//        int sparseTracks = drumTrack.trackController.tracks.Count(t => t.GetNoteDensity() <= 2);
        switch (current)
        {
            case MusicalPhase.Establish:
                MoveToNextPhase(specificPhase: MusicalPhase.Evolve);
                if (denseTracks > 0)
                {
//                    ExpandLoopOnDensestTrack();
                }
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
//                    ClearDensestTrack();
//                    MaybeShrinkLoopIfExpanded();
                    MoveToNextPhase(filter: g =>
                        g.phase == MusicalPhase.Evolve || g.phase == MusicalPhase.Wildcard);
                break;
            case MusicalPhase.Wildcard:
                // Bring it back into control
//                ClearRandomTrack();
//                ShiftNoteSetRootForAllTracks();
                MoveToNextPhase(specificPhase: MusicalPhase.Pop);
                break;
            case MusicalPhase.Pop:
//                AdvanceChordsInNoteSets();
                MoveToNextPhase(specificPhase: MusicalPhase.Evolve);
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
    public void SetPhase(MusicalPhase targetPhase)
    {
        var group = phaseQueue.phaseGroups.FirstOrDefault(g => g.phase == targetPhase);
        if (group == null)
        {
            Debug.LogWarning($"Phase {targetPhase} not found in queue.");
            return;
        }

        currentPhaseIndex = phaseQueue.phaseGroups.IndexOf(group);
        MusicalPhaseGroup selectedGroup = phaseQueue.phaseGroups[currentPhaseIndex];

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
    public MusicalPhase GetCurrentPhase()
    {
        if (phaseQueue == null || phaseQueue.phaseGroups.Count == 0)
        {
            Debug.LogWarning("[MineNodeProgressionManager] No phase queue defined.");
            return MusicalPhase.Establish; // Fallback
        }

        if (currentPhaseIndex < 0 || currentPhaseIndex >= phaseQueue.phaseGroups.Count)
        {
            Debug.LogWarning("[MineNodeProgressionManager] Invalid currentPhaseIndex.");
            return MusicalPhase.Establish;
        }

        return phaseQueue.phaseGroups[currentPhaseIndex].phase;
    }

    public void OnMinedObjectCollected()
    {
        totalMinedObjectsCollected++;
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

    private bool ShouldAdvanceMineNodeSet()
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
        }
    }
}