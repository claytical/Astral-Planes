using System.Collections.Generic;

/// <summary>
/// A snapshot of how musically busy each active role/voice is for the current motif, computed
/// once per motif from each track's pre-generated NoteSets. Consumed by the maze generator to
/// scale base-pattern params (MazeDensityModulation) and per-role Voronoi territory sizing/
/// feature selection (CosmicDustGenerator.MazeGeneration.cs) — see the "Motif-Density-Driven
/// Cosmic Dust Maze Redesign" plan for the full formula rationale.
/// </summary>
public sealed class DustMotifDensitySnapshot
{
    public float MotifBusyness01;
    public Dictionary<RoleVoiceKey, float> WeightNorm01ByVoice = new();
    public Dictionary<MusicalRole, float> WeightNorm01ByRole = new();
    public Dictionary<RoleVoiceKey, float> AvgNoteCountByVoice = new();

    public float GetWeightNorm01ByVoice(RoleVoiceKey voice) =>
        WeightNorm01ByVoice.TryGetValue(voice, out var w) ? w : 0.5f;

    public float GetWeightNorm01ByRole(MusicalRole role) =>
        WeightNorm01ByRole.TryGetValue(role, out var w) ? w : 0.5f;

    public float GetAvgNoteCount(RoleVoiceKey voice) =>
        AvgNoteCountByVoice.TryGetValue(voice, out var v) ? v : 0f;
}

public static class MotifNoteDensityAnalyzer
{
    /// <summary>
    /// Averages each active voice's NoteSet.persistentTemplate.Count across all of its
    /// pre-generated bins (smoothing out per-bin round-robin config swaps — see
    /// MotifProfile.GetConfigForRoleAtBin), then derives:
    ///  - MotifBusyness01: the mean per-voice density normalized against [minNotesPerBinPerVoice,
    ///    maxNotesPerBinPerVoice] (an external reference scale, since there's no other motif to
    ///    compare against at generation time).
    ///  - WeightNorm01ByVoice/ByRole: each voice's/role's density relative to this motif's own
    ///    mean, saturating at 2x average. Never a literal note-count-to-cell-count mapping —
    ///    always an indirect, bounded 0-1 weight feeding downstream clamped formulas.
    /// </summary>
    public static DustMotifDensitySnapshot Analyze(
        MotifProfile motif,
        IReadOnlyList<InstrumentTrack> tracks,
        float minNotesPerBinPerVoice,
        float maxNotesPerBinPerVoice)
    {
        var snapshot = new DustMotifDensitySnapshot();

        var voices = motif?.GetActiveVoices();
        if (voices == null || voices.Count == 0 || tracks == null || tracks.Count == 0)
            return snapshot;

        var avgByVoice = new Dictionary<RoleVoiceKey, float>(voices.Count);
        foreach (var voice in voices)
        {
            var track = FindTrack(tracks, voice);
            if (track == null) continue;
            avgByVoice[voice] = AverageNoteCount(track);
        }

        if (avgByVoice.Count == 0) return snapshot;

        foreach (var kv in avgByVoice) snapshot.AvgNoteCountByVoice[kv.Key] = kv.Value;

        float sum = 0f;
        foreach (var kv in avgByVoice) sum += kv.Value;
        float motifMean = sum / avgByVoice.Count;

        snapshot.MotifBusyness01 = UnityEngine.Mathf.Clamp01(
            maxNotesPerBinPerVoice > minNotesPerBinPerVoice
                ? UnityEngine.Mathf.InverseLerp(minNotesPerBinPerVoice, maxNotesPerBinPerVoice, motifMean)
                : 0.5f);

        foreach (var kv in avgByVoice)
        {
            float relative = motifMean > 0.0001f ? kv.Value / motifMean : 1f;
            snapshot.WeightNorm01ByVoice[kv.Key] = UnityEngine.Mathf.Clamp01(relative / 2f);
        }

        var sumByRole = new Dictionary<MusicalRole, float>();
        var countByRole = new Dictionary<MusicalRole, int>();
        foreach (var kv in avgByVoice)
        {
            var role = kv.Key.role;
            sumByRole.TryGetValue(role, out float existingSum);
            sumByRole[role] = existingSum + kv.Value;
            countByRole.TryGetValue(role, out int existingCount);
            countByRole[role] = existingCount + 1;
        }
        foreach (var kv in sumByRole)
        {
            float roleAvg = kv.Value / countByRole[kv.Key];
            float relative = motifMean > 0.0001f ? roleAvg / motifMean : 1f;
            snapshot.WeightNorm01ByRole[kv.Key] = UnityEngine.Mathf.Clamp01(relative / 2f);
        }

        return snapshot;
    }

    private static float AverageNoteCount(InstrumentTrack track)
    {
        int bins = UnityEngine.Mathf.Max(1, track.maxLoopMultiplier);
        int sum = 0;
        int counted = 0;
        for (int b = 0; b < bins; b++)
        {
            var noteSet = track.GetNoteSetForBin(b);
            if (noteSet?.persistentTemplate == null) continue;
            sum += noteSet.persistentTemplate.Count;
            counted++;
        }
        return counted > 0 ? sum / (float)counted : 0f;
    }

    private static InstrumentTrack FindTrack(IReadOnlyList<InstrumentTrack> tracks, RoleVoiceKey voice)
    {
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            if (t != null && t.assignedRole == voice.role && t.voiceIndex == voice.voiceIndex)
                return t;
        }
        return null;
    }
}
