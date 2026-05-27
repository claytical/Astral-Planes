using System.Collections.Generic;
using UnityEngine;

public partial class InstrumentTrack
{
    private void Harmony_Bins_EnsureSize(int want)
{
    if (want <= 0) want = 1;

    // Fill-order list
    if (_binFillOrder == null)
    {
        _binFillOrder = new List<int>(want);
        for (int i = 0; i < want; i++) _binFillOrder.Add(0);
    }
    else if (_binFillOrder.Count != want)
    {
        if (_binFillOrder.Count < want)
        {
            int add = want - _binFillOrder.Count;
            for (int i = 0; i < add; i++) _binFillOrder.Add(0);
        }
        else
        {
            _binFillOrder.RemoveRange(want, _binFillOrder.Count - want);
        }
    }

    // Chord-index list
    if (_binChordIndex == null)
    {
        _binChordIndex = new List<int>(want);
        for (int i = 0; i < want; i++) _binChordIndex.Add(-1);
    }
    else if (_binChordIndex.Count != want)
    {
        if (_binChordIndex.Count < want)
        {
            int add = want - _binChordIndex.Count;
            for (int i = 0; i < add; i++) _binChordIndex.Add(-1);
        }
        else
        {
            _binChordIndex.RemoveRange(want, _binChordIndex.Count - want);
        }
    }

    // _nextFillOrdinal only increments on first-time fills; no rewind here.
    if (_nextFillOrdinal < 1) _nextFillOrdinal = 1;
}
    private void Harmony_OnBinFilled(int binIndex, int progressionLength)
    {
        if (binIndex < 0) return;
        if (_binFillOrder == null) return;
        if (binIndex >= _binFillOrder.Count) return;

        if (_binFillOrder[binIndex] == 0)
            _binFillOrder[binIndex] = _nextFillOrdinal++;
    }
    private void Harmony_OnBinEmptied(int binIndex)
    {
        if (binIndex < 0) return;
        if (_binFillOrder == null) return;
        if (binIndex >= _binFillOrder.Count) return;

        // Fill order is a state label (for UI/logic about "filled").
        _binFillOrder[binIndex] = 0;

        // DO NOT clear _binChordIndex here.
        // Chord identity is deterministic per bin index (or authored override), not fill state.
    }
    public int Harmony_GetChordIndexForBin(int binIndex)
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var hd = _gfm?.harmony;
        if (hd == null) return -1;

        int progLen = Mathf.Max(1, hd.ProgressionLength);
        if (binIndex < 0) return -1;

        // Optional: honor an authored override if present and valid.
        if (_binChordIndex != null && binIndex < _binChordIndex.Count)
        {
            int authored = _binChordIndex[binIndex];
            if (authored >= 0) return authored;
        }

        // Deterministic: absolute bin -> progression slot
        return ((binIndex % progLen) + progLen) % progLen;
    }

    private static List<int> BuildChordTones(Chord chord, int lowestNote, int highestNote)
    {
        var allowed = new List<int>(64);
        for (int oct = -2; oct <= 3; oct++)
        {
            for (int k = 0; k < chord.intervals.Count; k++)
            {
                int n = chord.rootNote + chord.intervals[k] + 12 * oct;
                if (n >= lowestNote && n <= highestNote)
                    allowed.Add(n);
            }
        }
        return allowed;
    }

    private static int SnapToNearestChordTone(int shifted, List<int> allowedSorted)
    {
        int best = allowedSorted[0];
        int bestDist = Mathf.Abs(best - shifted);
        for (int i = 1; i < allowedSorted.Count; i++)
        {
            int d = Mathf.Abs(allowedSorted[i] - shifted);
            if (d < bestDist) { best = allowedSorted[i]; bestDist = d; }
        }
        return best;
    }

    private int QuantizeNoteToBinChord(int stepIndex, int midiNote, int authoredRootMidi = int.MinValue) {
    // Resolve which bin this step belongs to
    int bin = BinIndexForStep(stepIndex);

    // Use the NoteSet's chordRegion — it carries delta-adjusted absolute roots
    // (NoteSetFactory applies keyRootMidi − authoredFirst to every chord root).
    // HarmonyDirector.profile stores the raw ScriptableObject values, which may be
    // relative scale degrees and would therefore produce a wrong rootDelta.
    var ns = GetNoteSetForBin(bin);
    var region = ns?.chordRegion;
    Chord chord, baseChord;
    if (region != null && region.Count > 0)
    {
        int chordIdx = bin % region.Count;
        chord     = region[chordIdx];
        baseChord = region[0];
    }
    else
    {
        // Fallback: try HarmonyDirector (works when chordRegion is absent)
        int chordIdx = Harmony_GetChordIndexForBin(bin);
        if (chordIdx < 0) return midiNote;
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var hd = _gfm?.harmony;
        if (hd == null) return midiNote;
        if (!hd.TryGetChordAt(chordIdx, out chord)) return midiNote;
        if (!hd.TryGetChordAt(0, out baseChord)) baseChord = chord;
    }

    // rootDelta: authored note's root → target chord's root
    int rootDelta = (authoredRootMidi != int.MinValue)
        ? chord.rootNote - authoredRootMidi
        : chord.rootNote - baseChord.rootNote;

    if (Mathf.Abs(rootDelta) > 3)
        Debug.LogWarning($"[QUANTIZE:DELTA] track={name} step={stepIndex} note={midiNote} chord.root={chord.rootNote} authoredRoot={authoredRootMidi} rootDelta={rootDelta}");

    bool noteAlreadyInTargetChord = false;
    if (chord.intervals != null && chord.intervals.Count > 0)
    {
        int notePc = ((midiNote % 12) + 12) % 12;
        int chordRootPc = ((chord.rootNote % 12) + 12) % 12;
        for (int i = 0; i < chord.intervals.Count; i++)
        {
            int ivPc = ((chord.intervals[i] % 12) + 12) % 12;
            if (notePc == ((chordRootPc + ivPc) % 12))
            {
                noteAlreadyInTargetChord = true;
                break;
            }
        }
    }

    // If note already fits this chord, preserve it and only octave-fit into track range.
    // Otherwise apply root-delta transposition for bin/chord movement.
        int shifted = noteAlreadyInTargetChord ? midiNote : (midiNote + rootDelta);
        shifted = ShiftByOctavesIntoTrackRange(shifted);

        var allowed = BuildChordTones(chord, lowestAllowedNote, highestAllowedNote);
        if (allowed.Count == 0) return shifted;
        allowed.Sort();

        return ShiftByOctavesIntoTrackRange(SnapToNearestChordTone(shifted, allowed));
}

    public int QuantizeNoteForStep(int stepIndex, int rawMidi, int authoredRootMidi)
        => QuantizeNoteToBinChord(stepIndex, rawMidi, authoredRootMidi);

    public void PlayQuantizedNoteForStep(int stepIndex, int rawMidi, int durationTicks, float velocity)
    {
        int binSize   = Mathf.Max(1, BinSize());
        int binIdx    = BinIndexForStep(stepIndex);
        int localStep = ((stepIndex % binSize) + binSize) % binSize;

        int authoredRoot = int.MinValue;
        if (_binNoteSets != null && binIdx >= 0 && binIdx < _binNoteSets.Length && _binNoteSets[binIdx] != null)
            authoredRoot = _binNoteSets[binIdx].GetAuthoredRootMidi(localStep);
        if (authoredRoot == int.MinValue)
            authoredRoot = GetAuthoredRootMidiInRegister(rawMidi);

        PlayNote127(QuantizeNoteToBinChord(stepIndex, rawMidi, authoredRoot), durationTicks, velocity);
    }

    /// <summary>
    /// Moves a MIDI note by octaves to fit this track's playable range.
    /// If the range is narrower than one octave and no exact octave fit exists,
    /// falls back to clamping.
    /// </summary>
    private int ShiftByOctavesIntoTrackRange(int midiNote)
    {
        int low = Mathf.Min(lowestAllowedNote, highestAllowedNote);
        int high = Mathf.Max(lowestAllowedNote, highestAllowedNote);
        if (high <= low) return low;

        int shifted = midiNote;

        while (shifted < low && shifted + 12 <= high)
            shifted += 12;
        while (shifted > high && shifted - 12 >= low)
            shifted -= 12;

        if (shifted < low || shifted > high)
            shifted = Mathf.Clamp(shifted, low, high);

        return shifted;
    }
}
