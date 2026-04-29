using System.Collections.Generic;

/// <summary>
/// Invariants:
/// - Note commit time keys are absolute step indices present in the loop at commit time.
/// - Bin note set array length always matches max-loop-multiplier sizing requested by caller.
/// - Loop notes are the source of truth for persistent authored/committed notes.
/// </summary>
public sealed class TrackLoopState
{
    public readonly List<(int stepIndex, int note, int duration, float velocity, int authoredRootMidi)> PersistentLoopNotes = new();
    public readonly Dictionary<int, float> NoteCommitTimes = new();

    private NoteSet[] _binNoteSets;

    public void EnsureBinNoteSetCapacity(int maxLoopMultiplier)
    {
        if (_binNoteSets == null || _binNoteSets.Length != maxLoopMultiplier)
            _binNoteSets = new NoteSet[maxLoopMultiplier];
    }

    public void SetNoteSetForBin(int binIndex, NoteSet noteSet, int maxLoopMultiplier)
    {
        EnsureBinNoteSetCapacity(maxLoopMultiplier);
        if (binIndex >= 0 && binIndex < _binNoteSets.Length)
            _binNoteSets[binIndex] = noteSet;
    }

    public NoteSet GetNoteSetForBin(int binIndex, NoteSet fallback)
    {
        if (_binNoteSets != null && binIndex >= 0 && binIndex < _binNoteSets.Length)
        {
            var ns = _binNoteSets[binIndex];
            if (ns != null) return ns;
        }

        return fallback;
    }

    public void RecordCommittedNote(int stepIndex, int note, int durationTicks, float velocity, int authoredRootMidi, float commitTime)
    {
        PersistentLoopNotes.Add((stepIndex, note, durationTicks, velocity, authoredRootMidi));
        NoteCommitTimes[stepIndex] = commitTime;
    }

    public void ClearAll()
    {
        PersistentLoopNotes.Clear();
        NoteCommitTimes.Clear();
        _binNoteSets = null;
    }
}
