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

    private void Awake()
    {
        drumTrack = GetComponent<DrumTrack>();
        trackController = GetComponent<InstrumentTrackController>();

        if (!drumTrack || !trackController)
            Debug.LogError("MineNodeProgressionManager is missing required components on the same GameObject.");
    }

    public void OnMinedObjectCollected()
    {
        totalMinedObjectsCollected++;
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