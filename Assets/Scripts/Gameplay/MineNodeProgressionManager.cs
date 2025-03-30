using System.Linq;
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
    
    public void MoveToNextPhase()
    {
        drumTrack.ClearAllActiveMineNodes();
        currentPhaseIndex++;

        if (currentPhaseIndex >= phaseQueue.phaseGroups.Count)
        {
            Debug.Log("🎵 All phases completed — looping or staying final.");
            return;
        }

        var phaseGroup = phaseQueue.phaseGroups[currentPhaseIndex];

        if (phaseGroup.allowRandomSelection && phaseGroup.spawnerOptions.Count > 0)
        {
            currentSpawnerSet = phaseGroup.spawnerOptions[Random.Range(0, phaseGroup.spawnerOptions.Count)];
        }
        else if (phaseGroup.spawnerOptions.Count > 0)
        {
            currentSpawnerSet = phaseGroup.spawnerOptions[0];
        }

        Debug.Log($"➡️ Phase advanced to {phaseGroup.phase}, using spawner set: {currentSpawnerSet.name}");
        drumTrack.SetMineNodeSpawnerSet(currentSpawnerSet);
    }
    public void EvaluateProgression()
    {
        var current = phaseQueue.phaseGroups[currentPhaseIndex].phase;
        int totalNotes = GetTotalNoteCount();
        int denseTracks = drumTrack.trackController.tracks.Count(t => t.GetNoteDensity() >= 6);
        int sparseTracks = drumTrack.trackController.tracks.Count(t => t.GetNoteDensity() <= 2);


        switch (current)
        {
            case SpawnerPhase.Intro:
                if (totalNotes >= 6)
                    MoveToNextPhase();
                break;
            case SpawnerPhase.Reharmonize:
                if (denseTracks >= 2 && sparseTracks >= 1)
                {
                    MoveToNextPhase();
                }
                break;
            case SpawnerPhase.GrooveStart:
                if (drumTrack.GetCollectedStarCount() >= 4)
                    MoveToNextPhase();
                break;
            case SpawnerPhase.InstrumentChoice:
                if (totalNotes >= 20)
                    MoveToNextPhase();
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
                Random.Range(0, selectedGroup.spawnerOptions.Count)
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