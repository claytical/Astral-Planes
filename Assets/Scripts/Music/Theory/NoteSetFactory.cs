using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
public readonly struct StepDomain
{
    public readonly int stepsPerBin;   // canonical, e.g. 16
    public readonly int bins;          // how many bins you want the set to span during generation
    public int stepsTotal => stepsPerBin * bins;

    public StepDomain(int stepsPerBin, int bins)
    {
        this.stepsPerBin = Mathf.Max(1, stepsPerBin);
        this.bins = Mathf.Max(1, bins);
    }
}
public class NoteSetFactory : MonoBehaviour
{

    public NoteSet Generate(InstrumentTrack track, MotifProfile motif, int entropy = 0)
    {
        return GenerateForBin(track, motif, binIndex: 0, entropy: entropy);
    }
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

    var rng  = SessionGenome.For($"{motif.name}-{track.assignedRole}-{track.GetInstanceID()}-b{binIndex}-n{entropy}"); string key = $"{motif.name}/{track.assignedRole}/bin{binIndex}";
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
        int authoredFirst = motif.chordProgression.chordSequence[0].rootNote;
        int chordDelta = rootMidi - authoredFirst;

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

    int totalSteps = GetAuthoritativeStepsPerLoop(track);

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
            persistentTemplate = new List<(int step, int note, int duration, float vel, int authoredRootMidi)>()
        };
        silent.Initialize(track, totalSteps);
        return silent;
    }

    var riff = cfg.riff.riff;

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
            persistentTemplate = new List<(int step, int note, int duration, float vel, int authoredRootMidi)>()
        };
        silent.Initialize(track, totalSteps);
        return silent;
    }

    Debug.Log(
        $"[NoteSetFactory] RIFF MODE track=’{track.name}’ cfg=’{cfg.name}’ riffAsset=’{cfg.riff.name}’ " +
        $"events={riff.events.Count} riff.authoredRootMidi={riff.authoredRootMidi} octaveShift={riff.octaveShift} " +
        $"totalSteps={totalSteps} debugKey={debugKey} binIndex={binIndex}"
    );

    const int stepsPerBar = 16;
    const int beatsPerBar = 4;
    const int ticksPerQuarter = 480;
    int ticksPerStep = ticksPerQuarter / (stepsPerBar / beatsPerBar);

    int authoredAnchor = riff.authoredRootMidi > 0 ? riff.authoredRootMidi : rootMidi;
    int targetRoot = (chordSeq != null && chordSeq.Count > 0)
        ? chordSeq[binIndex % chordSeq.Count].rootNote
        : rootMidi;
    int normalizedTarget = targetRoot;
    while (normalizedTarget > authoredAnchor + 11) normalizedTarget -= 12;
    while (normalizedTarget < authoredAnchor)       normalizedTarget += 12;
    int delta = normalizedTarget - authoredAnchor + riff.octaveShift * 12;

    Debug.Log(
        $"[NoteSetFactory] RIFF TRANSPOSE bin={binIndex} authoredAnchor={authoredAnchor} " +
        $"targetRoot={targetRoot} delta={delta} (riff.authoredRootMidi was {riff.authoredRootMidi})"
    );

    var ordered = riff.events.OrderBy(e => e.step).ToList();
    var riffPersistent = new List<(int step, int note, int duration, float vel, int authoredRootMidi)>(ordered.Count);

    // Pre-pass: find the highest transposed note and compute a uniform octave
    // shift so it fits within the track range. Applying the same shift to every
    // note preserves the relative intervals inside the riff; per-note ShiftIntoRange
    // below is kept as a safety net for extreme cases.
    int maxTransposed = int.MinValue;
    for (int i = 0; i < ordered.Count; i++)
    {
        var e = ordered[i];
        if (e.step >= 0 && e.step < totalSteps && e.midiNote + delta > maxTransposed)
            maxTransposed = e.midiNote + delta;
    }
    int blockShift = (maxTransposed > int.MinValue)
        ? ShiftIntoRange(maxTransposed, track.lowestAllowedNote, track.highestAllowedNote) - maxTransposed
        : 0;

    for (int i = 0; i < ordered.Count; i++)
    {
        var e = ordered[i];

        int step = e.step;
        if (step < 0 || step >= totalSteps) continue;
        int midi = e.midiNote + delta + blockShift;

        midi = ShiftIntoRange(midi, track.lowestAllowedNote, track.highestAllowedNote);

        int durTicks = Mathf.Max(ticksPerStep, e.durSteps * ticksPerStep);
        float vel127 = Mathf.Clamp01(e.velocity01) * 127f;
        riffPersistent.Add((step, midi, durTicks, vel127, targetRoot));
    }

    var riffNs = new NoteSet
    {
        assignedInstrumentTrack = track,
        rootMidi = rootMidi,
        chordRegion = chordSeq,
        persistentTemplate = riffPersistent
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
