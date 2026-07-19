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

    const int stepsPerBar = 16;
    const int beatsPerBar = 4;
    const int ticksPerQuarter = 480;
    int ticksPerStep = ticksPerQuarter / (stepsPerBar / beatsPerBar);

    int authoredAnchor = riff.authoredRootMidi > 0 ? riff.authoredRootMidi : chordSeq[0].rootNote;
    int targetRoot = chordSeq[binIndex % chordSeq.Count].rootNote;
    // Exact-register transposition: the chord root's octave is authoritative.
    // No nearest-register normalization — a ♭III authored an octave below I
    // must pull the riff down with it.
    int delta = targetRoot - authoredAnchor + riff.octaveShift * 12;

    if (GameFlowManager.VerboseLogging) Debug.Log(
        $"[NoteSetFactory] RIFF TRANSPOSE bin={binIndex} authoredAnchor={authoredAnchor} " +
        $"targetRoot={targetRoot} delta={delta} (riff.authoredRootMidi was {riff.authoredRootMidi})"
    );

    var ordered = riff.events.OrderBy(e => e.step).ToList();
    var riffPersistent = new List<(int step, int note, int duration, float vel, int authoredRootMidi)>(ordered.Count);

    for (int i = 0; i < ordered.Count; i++)
    {
        var e = ordered[i];

        int step = e.step;
        if (step < 0 || step >= totalSteps) continue;

        // Hybrid rule: the exact transposed note wins whenever it is playable;
        // only out-of-range notes get octave-shifted (per note, final note only).
        int midi = ShiftIntoRange(e.midiNote + delta, track.lowestAllowedNote, track.highestAllowedNote);

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
