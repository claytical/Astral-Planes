using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class NoteSetFactory : MonoBehaviour
{


    public NoteSet GenerateForBin(InstrumentTrack track, MotifProfile motif, int binIndex, int entropy = 0)
    {
    if (track == null)
    {
        Debug.LogWarning("[NoteSetFactory] Motif-based Generate called with null track.");
        return null;
    }

    if (motif == null)
    {
        // NOTE: this overload does not have phase context, so we cannot safely fall back here.
        Debug.LogWarning("[NoteSetFactory] Motif is null; cannot generate motif-based NoteSet.");
        return null;
    }
    var cfg = motif.GetConfigForRoleAtBin(track.assignedRole, binIndex, track.maxLoopMultiplier, track.voiceIndex);
    if (cfg == null)
    {
        Debug.LogWarning($"[NoteSetFactory] No RoleMotifNoteSetConfig for role {track.assignedRole} in motif {motif.name}. Cannot generate motif-based NoteSet.");
        return null;
    }

    var rng  = SessionGenome.For($"{motif.name}-{track.assignedRole}-{track.InstanceId}-b{binIndex}-n{entropy}"); string key = $"{motif.name}/{track.assignedRole}/bin{binIndex}";
    return GenerateFromMotifConfig(track, motif, cfg, rng, key, binIndex);
}
    private NoteSet GenerateFromMotifConfig(
       InstrumentTrack track,
       MotifProfile motif,
       RoleMotifNoteSetConfig cfg,
      System.Random rng,
      string debugKey,
      int binIndex = 0)
{
    int rootMidi = ResolveRootMidiInRange(
        motif != null ? motif.keyRootMidi : track.lowestAllowedNote,
        track.lowestAllowedNote,
        track.highestAllowedNote
    );

    // --------------------------
    // Resolve chordSeq:
    // Motif progression wins; otherwise default to I triad.
    // (Riff-only testing shouldn't require any other config.)
    // --------------------------
    List<Chord> chordSeq = null;

    int motifChordCount =
        (motif != null &&
         motif.chordProgression != null &&
         motif.chordProgression.chordSequence != null)
            ? motif.chordProgression.chordSequence.Count
            : 0;

    if (motifChordCount > 0)
    {
        // The progression's rootNote register is authoritative: re-key to the motif's
        // keyRootMidi only. Never octave-fit chord roots to the track range — the
        // playable range applies to final notes, not chord roots.
        int authoredFirst = motif.chordProgression.chordSequence[0].rootNote;
        int keyRoot = motif.keyRootMidi > 0 ? motif.keyRootMidi : rootMidi;
        int chordDelta = keyRoot - authoredFirst;

        chordSeq = new List<Chord>(motifChordCount);
        for (int i = 0; i < motifChordCount; i++)
        {
            var c = motif.chordProgression.chordSequence[i];
            chordSeq.Add(new Chord
            {
                rootNote = c.rootNote + chordDelta,
                intervals = (c.intervals != null && c.intervals.Count > 0)
                    ? new List<int>(c.intervals)
                    : new List<int> { 0, 4, 7 }
            });
        }
    }

    if (chordSeq == null || chordSeq.Count == 0)
    {
        chordSeq = new List<Chord>
        {
            new Chord { rootNote = rootMidi, intervals = new List<int> { 0, 4, 7 } }
        };
    }

    if (cfg.riff == null)
    {
        Debug.LogError(
            $"[NoteSetFactory] cfg.riff is null. " +
            $"track=’{track?.name}’ cfg=’{cfg?.name}’ motif=’{motif?.name}’ debugKey={debugKey}"
        );
        var silent = new NoteSet
        {
            assignedInstrumentTrack = track,
            rootMidi = rootMidi,
            chordRegion = chordSeq,
            persistentTemplate = new List<(int step, int note, int duration, float vel, int authoredRootMidi)>(),
            expireAfterLoops = cfg.mineNodeExpireAfterLoops
        };
        silent.Initialize(track, GetAuthoritativeStepsPerLoop(track));
        return silent;
    }

    var riff = cfg.riff.riff;

    int drumSteps  = GetAuthoritativeStepsPerLoop(track);
    int totalSteps = (riff.loopSteps > 0 && riff.loopSteps > drumSteps)
        ? riff.loopSteps
        : drumSteps;

    if (riff.events == null || riff.events.Count == 0)
    {
        Debug.LogError(
            $"[NoteSetFactory] Riff has no events. " +
            $"track=’{track?.name}’ riffAsset=’{cfg.riff.name}’ debugKey={debugKey}"
        );
        var silent = new NoteSet
        {
            assignedInstrumentTrack = track,
            rootMidi = rootMidi,
            chordRegion = chordSeq,
            persistentTemplate = new List<(int step, int note, int duration, float vel, int authoredRootMidi)>(),
            expireAfterLoops = cfg.mineNodeExpireAfterLoops
        };
        silent.Initialize(track, totalSteps);
        return silent;
    }

    if (GameFlowManager.VerboseLogging) Debug.Log(
        $"[NoteSetFactory] RIFF MODE track=’{track.name}’ cfg=’{cfg.name}’ riffAsset=’{cfg.riff.name}’ " +
        $"events={riff.events.Count} riff.authoredRootMidi={riff.authoredRootMidi} octaveShift={riff.octaveShift} " +
        $"totalSteps={totalSteps} debugKey={debugKey} binIndex={binIndex}"
    );

    if (riff.stepsPerBar <= 0)
        Debug.LogWarning(
            $"[NoteSetFactory] Riff '{cfg.riff.name}' has no recorded stepsPerBar (imported before this field existed) — " +
            $"assuming 16. Re-import via MIDI→Riff Importer to record the real value and avoid duration drift.");
    int stepsPerBar = riff.stepsPerBar > 0 ? riff.stepsPerBar : 16;
    const int beatsPerBar = 4;
    const int ticksPerQuarter = 480;
    int ticksPerStep = ticksPerQuarter / (stepsPerBar / beatsPerBar);

    int authoredAnchor = riff.authoredRootMidi > 0 ? riff.authoredRootMidi : chordSeq[0].rootNote;
    // track.ActiveProfile is the bin-0-resolved profile (set by PhaseTransitionManager
    // before this per-bin loop runs), so this agrees with what runtime quantization
    // later reads via _activeProfile — falls back to this bin's own cfg.roleProfile
    // only if ActiveProfile hasn't been resolved yet.
    bool sameRootAcrossBins = track.ActiveProfile != null
        ? track.ActiveProfile.sameRootAcrossBins
        : (cfg.roleProfile != null && cfg.roleProfile.sameRootAcrossBins);
    Chord targetChord = sameRootAcrossBins ? chordSeq[0] : chordSeq[binIndex % chordSeq.Count];

    // ByVoice roles (voiceIndex > 0): anchor this voice to its own chord tone — root for voice 0,
    // the chord's next interval for voice 1, etc. — instead of every voice converging on the bare
    // root. Without this, voices authored on different chord tones (e.g. root/third/fifth) all
    // transpose onto the exact same pitch, since delta only ever preserves pitch class relative to
    // the chord's rootNote. voiceIndex 0 (every currently-shipped ByBin/RandomPerBin motif) is
    // unaffected: chordToneOffset stays 0, matching prior behavior exactly.
    int chordToneOffset = 0;
    if (track.voiceIndex > 0 && targetChord.intervals != null && targetChord.intervals.Count > 0)
        chordToneOffset = targetChord.intervals[track.voiceIndex % targetChord.intervals.Count];
    int targetRoot = targetChord.rootNote + chordToneOffset;

    // Register-preserving transposition: each riff is authored at a deliberate register for
    // its role (e.g. bass low, harmony high), while every ChordProgressionProfile asset is
    // authored with its own roots clustered near a single arbitrary octave. Jumping straight
    // to targetRoot's absolute octave would drag every role toward that one octave regardless
    // of its authored register. Instead, only nudge by the minimal signed pitch-class distance
    // (-5..+6 semitones) needed to align with the target chord tone, keeping the riff in its
    // own authored octave. riff.octaveShift is then a genuine, additive full-octave move on
    // top of that — meant for compensating register after an instrument voice/preset change.
    int rawPitchDelta = ((targetRoot - authoredAnchor) % 12 + 12) % 12;
    int pitchClassDelta = rawPitchDelta > 6 ? rawPitchDelta - 12 : rawPitchDelta;
    int delta = pitchClassDelta + riff.octaveShift * 12;

    // Fold the WHOLE riff by one consistent number of octaves — never per-note, which would
    // scramble the composed line's intervals — but only as a safety net when the transposed
    // span genuinely doesn't fit the track's playable range at all. A span that already fits
    // is left alone, so the minimal pitch-class nudge above and any manual octaveShift survive
    // into the final pitch instead of being folded back out.
    int riffMinNote = riff.events.Min(e => e.midiNote);
    int riffMaxNote = riff.events.Max(e => e.midiNote);
    int spanLo = riffMinNote + delta;
    int spanHi = riffMaxNote + delta;
    int groupFoldOffset = 0;
    if (spanLo < track.lowestAllowedNote || spanHi > track.highestAllowedNote)
    {
        int rangeMid = (track.lowestAllowedNote + track.highestAllowedNote) / 2;
        int spanMid = (spanLo + spanHi) / 2;
        groupFoldOffset = Mathf.RoundToInt((rangeMid - spanMid) / 12f) * 12;
    }

    if (GameFlowManager.VerboseLogging) Debug.Log(
        $"[NoteSetFactory] RIFF TRANSPOSE bin={binIndex} voiceIndex={track.voiceIndex} authoredAnchor={authoredAnchor} " +
        $"chordRoot={targetChord.rootNote} chordToneOffset={chordToneOffset} targetRoot={targetRoot} delta={delta} " +
        $"groupFoldOffset={groupFoldOffset} (riff.authoredRootMidi was {riff.authoredRootMidi})"
    );

    var ordered = riff.events.OrderBy(e => e.step).ToList();
    var riffPersistent = new List<(int step, int note, int duration, float vel, int authoredRootMidi)>(ordered.Count);

    for (int i = 0; i < ordered.Count; i++)
    {
        var e = ordered[i];

        int step = e.step;
        if (step < 0 || step >= totalSteps) continue;

        int midi = Mathf.Clamp(e.midiNote + delta + groupFoldOffset, track.lowestAllowedNote, track.highestAllowedNote);

        int durTicks = Mathf.Max(ticksPerStep, e.durSteps * ticksPerStep);
        float vel127 = Mathf.Clamp01(e.velocity01) * 127f;
        riffPersistent.Add((step, midi, durTicks, vel127, targetRoot));
    }

    var riffNs = new NoteSet
    {
        assignedInstrumentTrack = track,
        rootMidi = rootMidi,
        chordRegion = chordSeq,
        persistentTemplate = riffPersistent,
        expireAfterLoops = cfg.mineNodeExpireAfterLoops
    };

    riffNs.Initialize(track, totalSteps);
    return riffNs;
}

    private int GetAuthoritativeStepsPerLoop(InstrumentTrack track)
    {
        // DrumTrack is the runtime authority for step-domain (matches BinSize/leader semantics).
        var gfm = GameFlowManager.Instance;
        var drum = gfm != null ? gfm.activeDrumTrack : null;

        if (drum != null && drum.totalSteps > 0)
            return drum.totalSteps;

        // Conservative fallback
        return 16;
    }

    /// <summary>
    /// Attempts to place a desired root MIDI note inside the track's playable range by octave-shifting.
    /// If the range is too narrow or shifting cannot land inside, clamps as a last resort.
    /// </summary>
    private static int ShiftIntoRange(int midi, int lo, int hi)
    {
        if (hi <= lo) return lo;
        while (midi < lo && midi + 12 <= hi) midi += 12;
        while (midi > hi && midi - 12 >= lo) midi -= 12;
        return Mathf.Clamp(midi, lo, hi);
    }

    private int ResolveRootMidiInRange(int desiredRootMidi, int lo, int hi)
    {
        if (lo > hi)
        {
            int tmp = lo;
            lo = hi;
            hi = tmp;
        }

        // Fast path
        if (desiredRootMidi >= lo && desiredRootMidi <= hi)
            return desiredRootMidi;

        int r = desiredRootMidi;

        // Shift up/down in octaves to try to land in range.
        // We prefer shifting towards the range rather than oscillating.
        if (r < lo)
        {
            while (r < lo) r += 12;
        }
        else if (r > hi)
        {
            while (r > hi) r -= 12;
        }

        // If we landed in range, great. Otherwise clamp.
        if (r >= lo && r <= hi)
            return r;

        return Mathf.Clamp(r, lo, hi);
    }


}
