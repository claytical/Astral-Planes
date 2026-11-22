using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A saved MotifSnapshot resolved into a playable loop: timing, a step table of notes per role
/// channel, presets and gain trims. Pure data — no players, no clock. Shared by RingPreviewPlayer
/// (live carousel playback / audio export) and RingMidiExporter so both hear the same thing.
/// </summary>
public class RingPlaybackPlan
{
    public struct NoteEvent
    {
        public int note, channel, vel127, durationMs;
        public MusicalRole role;
    }

    // Bass=0, Harmony=1, Lead=2, Groove=3, None=4, Rhythm=5
    public static readonly Dictionary<MusicalRole, int> RoleChannel = new()
    {
        { MusicalRole.Bass,    0 },
        { MusicalRole.Harmony, 1 },
        { MusicalRole.Lead,    2 },
        { MusicalRole.Groove,  3 },
        { MusicalRole.None,    4 },
        { MusicalRole.Rhythm,  5 },
    };

    public MotifProfile Motif;
    public float Bpm;
    public float StepDurationSec;
    public int   StepsPerLoop;
    public int   TotalSteps;
    public float LoopLengthSec;

    /// <summary>[step] → notes starting on that step.</summary>
    public List<List<NoteEvent>> Steps;

    /// <summary>Preset per MIDI channel (16 entries).</summary>
    public int[] ChannelPresets = new int[16];

    /// <summary>Profile resolved per role for this motif (live volumeMultiplier source).</summary>
    public readonly Dictionary<MusicalRole, MusicalRoleProfile> RoleProfiles = new();

    /// <summary>volumeMultiplier saved in the snapshot per role (fallback when no profile).</summary>
    public readonly Dictionary<MusicalRole, float> SnapshotTrims = new();

    /// <summary>Roles that have notes, in RoleChannel order.</summary>
    public readonly List<MusicalRole> ActiveRoles = new();

    /// <summary>Channel gain for a role: live profile volumeMultiplier, else the saved trim, else 1.</summary>
    public float TrimFor(MusicalRole role)
    {
        if (RoleProfiles.TryGetValue(role, out var prof) && prof != null) return Mathf.Max(0f, prof.volumeMultiplier);
        return SnapshotTrims.TryGetValue(role, out var t) ? Mathf.Max(0f, t) : 1f;
    }

    public static int ChannelFor(MusicalRole role) => RoleChannel.TryGetValue(role, out int ch) ? ch : 4;

    /// <summary>Null when the snapshot is empty or its phase/motif can't be resolved (warning logged).</summary>
    public static RingPlaybackPlan Build(MotifSnapshot snap, PhaseLibrary phaseLibrary)
    {
        if (snap == null || snap.CollectedNotes == null || snap.CollectedNotes.Count == 0 || snap.TotalSteps <= 0)
            return null;

        var motif = ResolveMotif(snap, phaseLibrary);
        if (motif == null) return null;

        var plan = new RingPlaybackPlan { Motif = motif };

        float bpm = snap.Bpm > 0f ? snap.Bpm : (motif.bpm > 0f ? motif.bpm : 120f);
        int stepsPerLoop = snap.TotalSteps > 0 ? snap.TotalSteps
            : (motif.stepsPerLoop > 0 ? motif.stepsPerLoop : 16);
        plan.Bpm = bpm;

        if (snap.StepDurationSec > 0f)
        {
            plan.StepDurationSec = snap.StepDurationSec;
        }
        else
        {
            // Legacy ring (saved before timing capture): re-derive from the MotifProfile.
            AudioClip drumClip = null;
            if (motif.intensityClipTags != null)
                foreach (var t in motif.intensityClipTags) { if (t.clip != null) { drumClip = t.clip; break; } }
            if (drumClip == null && motif.entryDrumLoops != null)
                foreach (var c in motif.entryDrumLoops) { if (c != null) { drumClip = c; break; } }

            plan.StepDurationSec = drumClip != null
                ? drumClip.length / stepsPerLoop
                : 60f / bpm * 4f / stepsPerLoop;
        }

        // Loop length = leader loop width at capture time; legacy rings infer it from
        // the max allocated bin count across roles, capped at 5 (4 primary + 1 alternate).
        int leaderBins = snap.LeaderBins;
        if (leaderBins <= 0)
        {
            var roleBinCounts = new Dictionary<MusicalRole, HashSet<int>>();
            if (snap.TrackBins != null)
            {
                foreach (var tb in snap.TrackBins)
                {
                    if (!roleBinCounts.TryGetValue(tb.Role, out var set))
                        roleBinCounts[tb.Role] = set = new HashSet<int>();
                    set.Add(tb.BinIndex);
                }
            }
            int maxBinsPerRole = 0;
            foreach (var kvp in roleBinCounts)
                maxBinsPerRole = Mathf.Max(maxBinsPerRole, kvp.Value.Count);
            leaderBins = Mathf.Min(maxBinsPerRole > 0 ? maxBinsPerRole : 1, 5);
        }

        plan.StepsPerLoop  = stepsPerLoop;
        plan.TotalSteps    = leaderBins * stepsPerLoop;
        plan.LoopLengthSec = plan.TotalSteps * plan.StepDurationSec;

        // Presets per channel — use the motif's own roleProfile, fall back to library.
        foreach (var kvp in RoleChannel)
        {
            MusicalRoleProfile roleProf = motif.GetInstrumentProfileForVoice(kvp.Key, 0)
                                          ?? MusicalRoleProfileLibrary.GetProfile(kvp.Key);
            if (roleProf != null) plan.RoleProfiles[kvp.Key] = roleProf;

            // Saved playback profile (what was actually heard live) wins over re-derivation.
            int presetToUse = roleProf != null ? roleProf.midiPreset : 0;
            bool havePreset = roleProf != null;
            if (snap.TrackBins != null)
                foreach (var tb in snap.TrackBins)
                    if (tb.Role == kvp.Key && tb.HasPlaybackProfile)
                    {
                        presetToUse = tb.MidiPreset;
                        havePreset = true;
                        plan.SnapshotTrims[kvp.Key] = tb.VolumeMultiplier;
                        break;
                    }

            if (havePreset && kvp.Value < 16)
            {
                plan.ChannelPresets[kvp.Value] = presetToUse;
                if (GameFlowManager.VerboseLogging) Debug.Log($"[PREVIEW] Channel {kvp.Value} ({kvp.Key}) Preset {presetToUse}");
            }
        }

        // Step table: notes at their absolute session step (silent regions where a track hadn't
        // expanded stay silent, matching the live loop structure).
        int total = plan.TotalSteps;
        var steps = new List<List<NoteEvent>>(total);
        for (int i = 0; i < total; i++) steps.Add(new List<NoteEvent>());
        plan.Steps = steps;

        foreach (var entry in snap.CollectedNotes)
        {
            if (entry.Step < 0 || entry.Step >= total) continue;

            var role = entry.HasRole ? entry.Role : ResolveRoleForNote(snap.TrackBins, entry.BinIndex, entry.TrackColor);

            float v01  = entry.Velocity > 1.01f ? entry.Velocity / 127f : entry.Velocity;

            // Session duration in MPTK ticks → ms via capture-time BPM (same math as
            // MidiVoice.PlayNoteTicks). 0 ⇒ legacy ring, filled by the fallback below.
            int dMs = entry.DurationTicks > 0
                ? Mathf.Max(10, Mathf.RoundToInt(entry.DurationTicks * (60000f / (bpm * 480f))))
                : 0;

            // Saved pitch is already the final chord-quantized note the player heard; don't re-snap.
            steps[entry.Step].Add(new NoteEvent
            {
                note       = Mathf.Clamp(entry.Note, 0, 127),
                channel    = ChannelFor(role),
                vel127     = ComputeVelocity(snap.TrackBins, role, v01),
                durationMs = dMs,
                role       = role,
            });
        }

        // Legacy fallback: notes without a stored duration get distance to next onset on the same channel.
        var channelNoteSteps = new Dictionary<int, List<int>>();
        for (int s = 0; s < total; s++)
            foreach (var e in steps[s])
            {
                if (!channelNoteSteps.TryGetValue(e.channel, out var cList))
                    channelNoteSteps[e.channel] = cList = new List<int>();
                if (cList.Count == 0 || cList[cList.Count - 1] != s)
                    cList.Add(s);
            }

        int floorMs = Mathf.Max(10, Mathf.RoundToInt(plan.StepDurationSec * 200f));
        foreach (var kvp in channelNoteSteps)
        {
            int ch       = kvp.Key;
            var chSteps  = kvp.Value;
            for (int i = 0; i < chSteps.Count; i++)
            {
                int thisStep = chSteps[i];
                int nextStep = chSteps[(i + 1) % chSteps.Count];
                int dist = nextStep > thisStep ? nextStep - thisStep : total - thisStep + nextStep;
                if (dist == 0) dist = total;
                int dMs = Mathf.Max(floorMs, Mathf.RoundToInt(dist * plan.StepDurationSec * 1000f * 0.95f));

                var row = steps[thisStep];
                for (int j = 0; j < row.Count; j++)
                {
                    var n = row[j];
                    if (n.channel == ch && n.durationMs == 0) { n.durationMs = dMs; row[j] = n; }
                }
            }
        }

        foreach (var kvp in RoleChannel)
        {
            bool has = false;
            for (int s = 0; s < total && !has; s++)
                foreach (var n in steps[s]) if (n.role == kvp.Key) { has = true; break; }
            if (has) plan.ActiveRoles.Add(kvp.Key);
        }

        return plan;
    }

    // Match on PhaseId AND the motif at MotifIndex, so phases that (wrongly) share an id can't
    // resolve to the first one in the list — the carousel matches rings by (PhaseId, MotifId) too.
    public static MotifProfile ResolveMotif(MotifSnapshot snap, PhaseLibrary phaseLibrary)
    {
        if (phaseLibrary == null)
        {
            Debug.LogWarning("[RingPlaybackPlan] phaseLibrary not assigned.");
            return null;
        }

        PhaseLibrary.Phase phase = null;
        if (!string.IsNullOrEmpty(snap.PhaseId))
        {
            bool MotifMatches(PhaseLibrary.Phase p) =>
                string.IsNullOrEmpty(snap.MotifId) ||
                (p.motifs != null && snap.MotifIndex >= 0 && snap.MotifIndex < p.motifs.Count &&
                 p.motifs[snap.MotifIndex] != null && p.motifs[snap.MotifIndex].motifId == snap.MotifId);

            phase = phaseLibrary.phases.Find(p => p != null && p.phaseId == snap.PhaseId && MotifMatches(p))
                 ?? phaseLibrary.phases.Find(p => p != null && p.phaseId == snap.PhaseId);
        }

        if (phase == null)
        {
            if (snap.PhaseIndex < 0 || snap.PhaseIndex >= phaseLibrary.phases.Count)
            {
                Debug.LogWarning($"[RingPlaybackPlan] PhaseIndex {snap.PhaseIndex} out of range and PhaseId '{snap.PhaseId}' not found.");
                return null;
            }

            phase = phaseLibrary.phases[snap.PhaseIndex];
            if (!string.IsNullOrEmpty(snap.PhaseId))
                Debug.LogWarning($"[RingPlaybackPlan] PhaseId '{snap.PhaseId}' not found; falling back to index {snap.PhaseIndex}.");
        }
        if (phase == null || phase.motifs == null || snap.MotifIndex < 0 || snap.MotifIndex >= phase.motifs.Count)
        {
            Debug.LogWarning($"[RingPlaybackPlan] MotifIndex {snap.MotifIndex} out of range.");
            return null;
        }
        return phase.motifs[snap.MotifIndex];
    }

    // Same tolerant, alpha-blind per-channel comparison MotifRingGlyphApplicator.RecordRings.cs
    // uses to assign a note to its ring — an exact Color key could silently miss and fall back to
    // default(MusicalRole) (== Bass), desyncing which ring a note's firework hit from its dip.
    private static MusicalRole ResolveRoleForNote(
        List<MotifSnapshot.TrackBinData> trackBins, int binIndex, Color noteColor)
    {
        if (trackBins == null) return default;
        foreach (var tb in trackBins)
        {
            if (tb.BinIndex != binIndex) continue;
            if (Mathf.Approximately(tb.TrackColor.r, noteColor.r) &&
                Mathf.Approximately(tb.TrackColor.g, noteColor.g) &&
                Mathf.Approximately(tb.TrackColor.b, noteColor.b))
                return tb.Role;
        }
        return default;
    }

    // Mirrors the live path: role [min,max] velocity range. volumeMultiplier is NOT applied here —
    // it's channel gain, same as MidiVoice.SetGainTrim on live tracks.
    // Legacy rings (no saved profile) keep the old fixed 40–100 curve.
    private static int ComputeVelocity(List<MotifSnapshot.TrackBinData> trackBins, MusicalRole role, float v01)
    {
        if (trackBins != null)
            foreach (var tb in trackBins)
            {
                if (tb.Role != role || !tb.HasPlaybackProfile) continue;
                float v = Mathf.Lerp(tb.MinVelocity, tb.MaxVelocity, Mathf.Clamp01(v01));
                return Mathf.Clamp(Mathf.RoundToInt(v), 1, 127);
            }
        return Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(40f, 127f, v01)), 40, 100);
    }
}
