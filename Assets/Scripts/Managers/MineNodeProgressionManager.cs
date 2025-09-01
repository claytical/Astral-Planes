using System.Linq;
using System;
using System.Collections.Generic;
using UnityEngine;
using Random = System.Random;

public class MineNodeProgressionManager : MonoBehaviour
{
    private DrumTrack drumTrack;
    private InstrumentTrackController trackController;

    [Header("Progression Settings")]
    public int currentSetIndex ;
    public int requiredNotesPerTrack = 1;
    public int minimumLoopCount;
    public bool requireLoopCount;
    public List<MusicalPhaseProfile> phaseProfiles;
    public GameObject darkStarPrefab;
    private int darkStarSpawnCount = 0;
    
    [Header("Tracking")]
    public int totalMinedObjectsCollected;
    public int loopsCompleted;
    public MusicalPhaseQueue phaseQueue;
    private int currentPhaseIndex;
    SpawnStrategyProfile currentSpawnStrategy;
    SpawnStrategyProfile overrideSpawnStrategy;

    public bool phaseLocked;
    public bool isPhaseInProgress;
    public bool isPhaseTransitioning;
    [HideInInspector] public bool pendingNextPhase = false;
    private bool awaitingDarkStar = false;
    private bool hasStartedFirstPhase = false;
    private DarkStar currentDarkStar;
    private MusicalPhase currentPhase;
    private Vector3 darkStarSpawnPoint = Vector3.zero;
    public bool phaseStarActive = false; // 🛡️ Blocks progression until PhaseStar completes

    public void SetDarkStarSpawnPoint(Vector3 pos) => darkStarSpawnPoint = pos;
    public Vector3 GetDarkStarSpawnPoint() => darkStarSpawnPoint;

    private void Awake()
    {
        drumTrack = GetComponent<DrumTrack>();
        trackController = GetComponent<InstrumentTrackController>();
        // 🌟 Register globally
        MusicalPhaseLibrary.InitializeProfiles(phaseProfiles);
        if (!drumTrack || !trackController)
            Debug.LogError("MineNodeProgressionManager is missing required components on the same GameObject.");
    }

    public RemixUtility GetRemixUtility()
    {
        int index =  UnityEngine.Random.Range(0, phaseQueue.phaseGroups[currentPhaseIndex].remixUtilities.Count);
        return phaseQueue.phaseGroups[currentPhaseIndex].remixUtilities[index];
    }
    public void MoveToNextPhase(
        MusicalPhase? specificPhase = null,
        Func<MusicalPhaseGroup, bool> filter = null
    )
    {
        Debug.Log($"🌀 MoveToNextPhase called. awaitingDarkStar: {awaitingDarkStar}, darkStarModeEnabled: {drumTrack.darkStarModeEnabled}");
        overrideSpawnStrategy = null;
        if (awaitingDarkStar)
        {
            awaitingDarkStar = false;
            PatchLoopIfNeeded();
            SpawnDarkStar(drumTrack.GetStarPosition());
            return;
        }

        if (isPhaseInProgress || isPhaseTransitioning || pendingNextPhase)
            return;

        pendingNextPhase = true;
        isPhaseInProgress = true;
        isPhaseTransitioning = false;

        drumTrack.ClearAllActiveMinedObjects();
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
        drumTrack.currentPhase = currentPhase;
        Debug.Log($"Moving to phase: {currentPhase}");
        awaitingDarkStar = drumTrack.darkStarModeEnabled;

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
        
        drumTrack.SpawnPhaseStar(currentPhase, currentSpawnStrategy);
        drumTrack.ScheduleDrumLoopChange(MusicalPhaseLibrary.GetRandomClip(currentPhase));
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

    public bool IsAwaitingDarkStar() => awaitingDarkStar;


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
    private void PatchLoopIfNeeded()
    {
        foreach (var track in drumTrack.trackController.tracks)
        {
            if (track.CollectedNotesCount < 2)
            {
                var noteSet = track.GetCurrentNoteSet();
                if (noteSet == null) continue;

                var steps = noteSet.GetStepList();
                var notes = noteSet.GetNoteList();
                if (steps.Count == 0 || notes.Count == 0) continue;

                for (int i = track.CollectedNotesCount; i < 2; i++)
                {
                    int step = steps[UnityEngine.Random.Range(0, steps.Count)];
                    int note = noteSet.GetNextArpeggiatedNote(step);
                    int duration = track.CalculateNoteDuration(step, noteSet);
                    float velocity = UnityEngine.Random.Range(60f, 100f);

                    track.GetPersistentLoopNotes().Add((step, note, duration, velocity));
                }

                Debug.Log($"Patched {track.assignedRole} with filler notes.");
            }
        }

        drumTrack.trackController.UpdateVisualizer();
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

    public void SpawnDarkStar(Vector3 worldPosition)
    {
        Debug.Log($"Spawning Dark Star at position: {worldPosition}");
        if (currentDarkStar != null) {
            Debug.Log("Attempted to spawn dark star but one is already active");
            return;
        }
        GameObject instance = Instantiate(darkStarPrefab, worldPosition, Quaternion.identity);
        DarkStar darkStar = instance.GetComponent<DarkStar>();
        if (darkStar != null)
        {
            darkStar.currentPhase = currentPhase;
            darkStar.Initialize(this);
            currentDarkStar = darkStar;
            darkStar.Begin();
        }
    }
    public void OnDarkStarComplete()
    {
        Debug.Log("✅ OnDarkStarComplete() called");
        awaitingDarkStar = false;
        isPhaseInProgress = false;
        drumTrack.isPhaseStarActive = false;

        StartCoroutine(drumTrack.WaitForPhaseStarToDieThenAdvance()); // ✅ always runs

        if (!drumTrack.darkStarModeEnabled)
        {
            Debug.Log("🌊 The River mode: deferring phase advancement to WaitForPhaseStarToDieThenAdvance()");
            return;
        }

        var nextPhase = PeekNextPhase();
        if (nextPhase.HasValue)
        {
            drumTrack.queuedPhase = nextPhase;
            drumTrack.RestructureTracksWithRemixLogic();
            drumTrack.ScheduleDrumLoopChange(MusicalPhaseLibrary.GetRandomClip(nextPhase.Value));
            Debug.Log($"🎯 Scheduled drum loop change to {nextPhase.Value}");
        }
        else
        {
            Debug.LogWarning("🎵 No next phase available after DarkStar.");
        }
    }




    public SpawnStrategyProfile GetCurrentSpawnerStrategyProfile()
    {
        return overrideSpawnStrategy ?? phaseQueue?.phaseGroups[currentPhaseIndex].spawnStrategies.FirstOrDefault();
    }

    public void OverrideSpawnerStrategy(SpawnStrategyProfile profile)
    {
        currentSpawnStrategy = profile;
        overrideSpawnStrategy = profile;
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
        /*
        if (!drumTrack.darkStarModeEnabled)
        {
            TryAdvanceSet();
            EvaluateProgression();
        }*/
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