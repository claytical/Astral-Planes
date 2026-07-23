using System;
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

    // rootDelta: authored note's root → target chord's root. Roots are exact MIDI
    // registers (a ♭III authored an octave below I yields a large negative delta by
    // design), so the delta is applied unconditionally — a pitch-class match with the
    // target chord must not skip it, or the progression's octave is discarded.
    int rootDelta = (authoredRootMidi != int.MinValue)
        ? chord.rootNote - authoredRootMidi
        : chord.rootNote - baseChord.rootNote;

        // Hybrid rule: keep the exact transposed note when playable; octave-fit only
        // when it falls outside the track range.
        int shifted = ShiftByOctavesIntoTrackRange(midiNote + rootDelta);

        var allowed = BuildChordTones(chord, lowestAllowedNote, highestAllowedNote);
        if (allowed.Count == 0) return shifted;
        allowed.Sort();

        return ShiftByOctavesIntoTrackRange(SnapToNearestChordTone(shifted, allowed));
}

    public void PlayQuantizedNoteForStep(int stepIndex, int rawMidi, int durationTicks, float velocity)
    {
        int binSize   = Mathf.Max(1, BinSize());
        int binIdx    = BinIndexForStep(stepIndex);
        int localStep = ((stepIndex % binSize) + binSize) % binSize;

        int authoredRoot = int.MinValue;
        if (_binNoteSets != null && binIdx >= 0 && binIdx < _binNoteSets.Length && _binNoteSets[binIdx] != null)
            authoredRoot = _binNoteSets[binIdx].GetAuthoredRootMidi(localStep);
        if (authoredRoot == int.MinValue)
            authoredRoot = GetAuthoredRootMidiExact();

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

    /// <summary>
    /// Per-bin retune: snaps each note to the nearest tone in its bin's assigned chord
    /// in the current HarmonyDirector progression. Use this instead of RetuneLoopToChord(chord0)
    /// when a profile change should respect the full chord progression across bins.
    /// </summary>
    public void RetuneLoopToCurrentProgression(bool forceHarmonyDirector = false)
    {
        if (persistentLoopNotes == null || persistentLoopNotes.Count == 0) return;

        var modified = new List<(int step, int note, int dur, float vel, int authoredRoot)>(persistentLoopNotes.Count);
        foreach (var (step, note, dur, vel, authoredRoot) in persistentLoopNotes)
        {
            int bin = BinIndexForStep(step);

            // When a new ChordProgressionProfile is being committed (forceHarmonyDirector=true),
            // skip the NoteSet's chordRegion — it was baked from the OLD progression and would
            // override the new one. Always read directly from HarmonyDirector in that case.
            var ns = GetNoteSetForBin(bin);
            var region = (!forceHarmonyDirector) ? ns?.chordRegion : null;
            Chord chord;
            Chord baseChord = default;
            if (region != null && region.Count > 0)
            {
                chord    = region[bin % region.Count];
                baseChord = region[0];
            }
            else
            {
                int chordIdx = Harmony_GetChordIndexForBin(bin);
                if (_gfm == null) _gfm = GameFlowManager.Instance;
                var hd = _gfm?.harmony;
                if (chordIdx < 0 || hd == null || !hd.TryGetChordAt(chordIdx, out chord))
                {
                    modified.Add((step, note, dur, vel, authoredRoot));
                    continue;
                }
                if (!hd.TryGetChordAt(0, out baseChord)) baseChord = chord;
            }

            if (step % BinSize() == 0)
            {
                if (GameFlowManager.VerboseLogging) Debug.Log($"[CHORD][TRK][Retune] track={name} step={step} bin={bin} chordRoot={chord.rootNote} intervals={(chord.intervals != null ? chord.intervals.Count : 0)} noteIn={note}");
            }

            var allowed = BuildChordTones(chord, lowestAllowedNote, highestAllowedNote);
            if (allowed.Count == 0) { modified.Add((step, note, dur, vel, authoredRoot)); continue; }
            allowed.Sort();

            int rootDelta = (authoredRoot != int.MinValue) ? chord.rootNote - authoredRoot : chord.rootNote - baseChord.rootNote;
            // Exact-register transposition: always apply the root delta. A pitch-class
            // match with the target chord must not skip it — that discards the octave
            // encoded in the progression. Octave-fit only if the result is unplayable.
            int shifted = ShiftByOctavesIntoTrackRange(note + rootDelta);
            int retuned = ShiftByOctavesIntoTrackRange(SnapToNearestChordTone(shifted, allowed));
            modified.Add((step, retuned, dur, vel, authoredRoot));
        }

        RebuildLoopFromModifiedNotes(modified, transform.position);

        // Force immediate cache rebuild so the scheduler uses retuned pitches from this
        // frame onward. Without this a script-execution-order race between DrumTrack.Update
        // (which fires OnLoopBoundary / retune) and InstrumentTrack.Update (which rebuilds
        // via boundarySerial) can cause the first steps of the new loop to play old pitches.
        RebuildLoopCache_FORCE();
        _loopCacheDirtyPending = false;
    }

    private void RebuildLoopFromModifiedNotes(List<(int, int, int, float, int)> modified, Vector3 _)
    {
        Debug.LogWarning(
            $"[TRK:CLEAR_LOOP] track={name} fn=RebuildLoopFromModifiedNotes " +
            $"modified={(modified != null ? modified.Count : -1)} " +
            $"persistentBefore={(persistentLoopNotes != null ? persistentLoopNotes.Count : -1)}\n" +
            Environment.StackTrace);

        // Preserve original commit times — chord retuning changes pitch but not when the
        // player collected the note. Restoring these keeps CommitTime01 non-zero in the
        // ring glyph so squiggles are visible after a chord change.
        var savedCommitTimes = new Dictionary<int, float>(_noteCommitTimes);

        persistentLoopNotes.Clear();
        _noteCommitTimes.Clear();
        _loopCacheDirtyPending = true;
        // Keep existing marker GameObjects alive — chord retune never changes step positions,
        // only pitches. ForceSyncMarkersToPersistentLoop at the end will reposition/reconcile.
        // Destroying here caused a deferred-destroy timing bug: markers appeared alive during
        // the sync but were deleted end-of-frame, leaving the track visually empty.
        _spawnedNotes.Clear();

        if (modified != null)
        {
            foreach (var (step, note, dur, vel, authoredRoot) in modified)
            {
                // skipChordQuantize=true: notes in `modified` are already at their final pitch;
                // re-quantizing here would double-process them and produce wrong results.
                AddNoteToLoop(step, note, dur, vel, true, authoredRoot, skipChordQuantize: true);
                if (savedCommitTimes.TryGetValue(step, out float originalTime))
                    _noteCommitTimes[step] = originalTime;
            }
        }

        // AddNoteToLoop above tries to update noteMarkers by key lookup, but those GameObjects
        // were just destroyed by the _spawnedNotes loop above. Dead Transform entries remain in
        // noteMarkers, so AddNoteToLoop finds them but skips creation (t != null fails).
        // ForceSyncMarkersToPersistentLoop purges dead entries and re-creates any missing markers.
        controller?.noteVisualizer?.ForceSyncMarkersToPersistentLoop(this);
    }
}
