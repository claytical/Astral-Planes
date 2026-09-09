using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

public enum TrapShape { Circle, Box }

/// <summary>
/// A complete musical motif: beat mood + chord progression + role strategies + per-role NoteSet configs.
/// This is what a PhaseStar should reference.
/// </summary>
[CreateAssetMenu(
    fileName = "MotifProfile",
    menuName = "Astral Planes/Motif/Motif Profile")]
public class MotifProfile : ScriptableObject
{
    [Header("Identity")]
    public string motifId;                  // Stable identifier (e.g. "RockSteady_A")
    public string displayName;             // Designer-friendly label

    public int keyRootMidi = 60;   // default C4
    [Header("Drums")]
    [Tooltip("Entry loops: minimal / nearly silent. One will be chosen at motif start.")]
    public List<AudioClip> entryDrumLoops = new();

    [Tooltip("Intensity loops tagged with a target (depth, breadth, pressure) point. Selection picks whichever tag is nearest the live-computed axis values.")]
    public List<IntensityClipTag> intensityClipTags = new();

    [Min(0), Tooltip("How many full drum loops to stay in the entry loop(s) before mapping intensity.")]
    public int entryLoopCount = 1;

    [Tooltip("When this motif starts, re-arm the first-carry hold: the entry loop plays until a vehicle carries a collectable again. If false, a session's first carry keeps intensity mapping active through this motif.")]
    public bool resetFirstCarryOnStart = false;

    [Header("Bridging Sample")]
    [Tooltip("One-shot sample(s) played when a vehicle touches a Wisp whose assigned exit leads into this motif — picked at random if more than one. Can be long (up to ~60s): meant to keep sounding as a bridge into this motif.")]
    public List<AudioClip> bridgeSamples = new();

    [Range(0f, 1f)] public float bridgeSampleVolume = 1f;

    [Tooltip("If true, delays playback to the next beat boundary (via DrumTrack.TryGetNextBaseStepDsp) instead of firing immediately on contact.")]
    public bool quantizeBridgeSampleToBeat = true;

    [Header("Timing")]
    public float bpm = 120f;
    public int stepsPerLoop = 16;
    [Min(0), Tooltip("Overrides DrumTrackConfig.defaultTransitionPhraseLengthLoops for transitions INTO this motif: how many completed leader-loop boundaries to wait before the swap is allowed to arm. 0 = use the config default.")]
    public int phraseLengthLoopsOverride = 0;

    [Header("Star Structure")]
    [Min(1), Tooltip("Total successful node harvests for this motif, shared across all active roles. Stars spawn reactively per role but never beyond this budget; expired/escaped/SuperNode outcomes refund their slot. The bridge unlocks once this many mine bursts have been collected.")]
    public int nodesPerStar = 4;

    [Tooltip("Star behavior profile for this motif (speed, drift, keep-clear, safety bubble, etc.). Overrides the phase-level personality registry.")]
    public PhaseStarBehaviorProfile starBehavior;

    [Header("Maze")]
    [Tooltip("Pattern config for maze generation while this motif is active. If null, falls back to FullFill.")]
    public MazePatternConfig mazePattern;

    [Header("Maze Geography")]
    [Tooltip("Per-role geographic feature overrides. When empty, feature defaults are derived from mazePattern.patternType (e.g. RingChokepoints → Rings, DrunkenStrokes → Archipelago). Add entries here only to break the natural pairing.")]
    public List<MazeRoleGeoConfig> roleGeoConfigs = new();

    [Range(0f, 1f), Tooltip("Fraction of filler cells (from genericFillRatio01's universal reservation, and/or exact-count mode's leftover cells) that become permanent filler dust — never regrows for the rest of this motif, darker gray. The remainder regrows normally on the standard cadence with the ordinary gray tint.")]
    public float genericFillNeverRegrowRatio = 1f;

    [Range(0f, 1f), Tooltip("Fraction of ALL occupied maze cells — every generation mode (default Voronoi, single-voice, exact-count) — reserved as generic filler BEFORE role-territory assignment runs. 0 = no universal filler (default; existing motifs unaffected). Composes additively with exact-count mode's own per-voice leftover-becomes-filler math: cells reserved here are removed from the pool exact-count voices claim from, so an exact-count motif can end up with MORE total filler than this ratio alone implies. Split into never-regrow/quick-regrow sub-types via genericFillNeverRegrowRatio, same as exact-count leftover.")]
    public float genericFillRatio01 = 0f;

    [Range(0f, 1f), Tooltip("Additional multiplier applied to carve/drain resistance ON TOP OF the already-softer None baseline (CosmicDustGeneratorConfig.noneCarveResistance01/noneDrainResistance01), applied ONLY when a generic-fill cell (hiddenRoleIsGenericFill) regrows back to None. Role-tinted regrowth is never affected. 1 = no extra softening (default).")]
    public float genericFillRegrowSoftenMultiplier01 = 1f;

    [Header("Harmony")]
    [Tooltip("Chord progression used while this motif is active.")]
    public ChordProgressionProfile chordProgression;

    [Tooltip("If set, collecting all SuperNode shards triggers this progression at the next loop boundary.")]
    public ChordProgressionProfile alternateChordProgressionProfile;

    [Header("Per-role NoteSet configs")]
    [Tooltip("Per-role NoteSet generation configs for this motif.")]
    public List<RoleMotifNoteSetConfig> roleNoteConfigs = new List<RoleMotifNoteSetConfig>();

    [Header("Selection")]
    [Tooltip("Optional weight for random selection in a MotifLibrary.")]
    public float selectionWeight = 1f;
    
    [Tooltip("How many entries at the front of beatProfileSequence play once per motif, then are skipped on wrap.")]
    public int beatIntroCount = 0;

    [FormerlySerializedAs("driveBeatsFromEnergy")]
    [Tooltip("If true, drums are driven by live CosmicDust density through beatProfileSequence.")]
    public bool driveBeatsFromDensity = true;

    [Header("Vehicle Trap")]
    [Tooltip("If true, a ring or box of CosmicDust spawns around each vehicle when this motif begins.")]
    public bool spawnVehicleTrap = false;
    public TrapShape trapShape = TrapShape.Circle;
    [Min(1), Tooltip("Ring radius in grid cells (circle) or half-side length (box).")]
    public int trapRadius = 3;
    [Min(0.01f)] public float trapGrowSeconds = 0.4f;
    [Tooltip("MusicalRole assigned to trap dust. Affects color and pluck sound.")]
    public MusicalRole trapRole = MusicalRole.None;

    private void OnValidate() {
        if (beatIntroCount < 0) beatIntroCount = 0;
        if (entryDrumLoops == null) entryDrumLoops = new List<AudioClip>();
        if (intensityClipTags == null) intensityClipTags = new List<IntensityClipTag>();
        if (bridgeSamples == null) bridgeSamples = new List<AudioClip>();
        if (beatIntroCount > entryDrumLoops.Count) beatIntroCount = entryDrumLoops.Count;
        ValidateTimingAgainstClips();
    }

    // Warns when an authored clip's real length isn't a whole-number subdivision of what
    // bpm/stepsPerLoop implies for the full phrase. A clip shorter than the phrase is expected
    // to loop natively N times underneath it (see DrumTrack.GetAuthoredBaseLoopSeconds) — only
    // a non-integer ratio means audio-bound and note-duration timing will actually disagree,
    // which surfaces as an audible/visual glitch at motif transitions.
    private void ValidateTimingAgainstClips()
    {
        if (bpm <= 0f || stepsPerLoop <= 0) return;
        float impliedLoopSec = stepsPerLoop * (60f / bpm) / 4f; // 4 steps/quarter (16th-note grid)

        void Check(AudioClip clip, string label)
        {
            if (clip == null || clip.length <= 0f) return;
            int n = Mathf.Max(1, Mathf.RoundToInt(impliedLoopSec / clip.length));
            float expectedClipSec = impliedLoopSec / n;
            float tol = Mathf.Max(0.02f, expectedClipSec * 0.02f);
            if (Mathf.Abs(clip.length - expectedClipSec) > tol)
                Debug.LogWarning($"[MotifProfile:{name}] bpm={bpm} stepsPerLoop={stepsPerLoop} implies phrase={impliedLoopSec:F3}s (nearest valid subdivision: {n}x{expectedClipSec:F3}s) but '{label}' clip '{clip.name}' is {clip.length:F3}s.");
        }

        foreach (var c in entryDrumLoops) Check(c, "entryDrumLoops");
        foreach (var t in intensityClipTags) Check(t.clip, "intensityClipTags");
    }

    /// <summary>
    /// Returns the config to use for a given role and bin index.
    ///
    /// One config  → same config for every bin (only harmonic shift varies between bins).
    /// N configs, ByBin        → configs[binIndex % N] so each bin gets a distinct variation in round-robin order.
    /// N configs, RandomPerBin → one config drawn independently at random per bin, seeded from
    ///                           SessionGenome so the draw is stable for the whole play session
    ///                           but varies across separate playthroughs.
    ///
    /// Both modes are deterministic given (session, role, binIndex, voiceIndex): repeated calls with
    /// the same arguments always return the same config, which is essential for saturation detection,
    /// for DiscoveryTrackNode re-spawns being consistent with what was originally placed, and for the
    /// several independent bin-0 lookups elsewhere (track color, zap-count, density/void coloring)
    /// agreeing with each other and with the actual generated NoteSet.
    /// </summary>
    public RoleMotifNoteSetConfig GetConfigForRoleAtBin(MusicalRole role, int binIndex, int totalBins, int voiceIndex = 0)
    {
        if (roleNoteConfigs == null || roleNoteConfigs.Count == 0)
            return null;
        List<RoleMotifNoteSetConfig> matches = null;
        for (int i = 0; i < roleNoteConfigs.Count; i++)        {
            var cfg = roleNoteConfigs[i];
            if (cfg != null && cfg.role == role)
            {
                matches ??= new List<RoleMotifNoteSetConfig>();
                matches.Add(cfg);
            }
        }

        if (matches == null || matches.Count == 0) return null;

        // Single config: same pattern every bin and every voice.
        if (matches.Count == 1) return matches[0];

        // ByVoice roles: voice 0 always gets config[0], voice 1 gets config[1], etc.
        // Bins within a voice all use the same authored RiffAsset (chord tone stays constant).
        // Read the mode off the actually-matched config's own profile, not an arbitrary
        // same-role profile from MusicalRoleProfileLibrary (which can differ from this motif's).
        if (matches[0].roleProfile != null && matches[0].roleProfile.configSelectionMode == RoleConfigSelectionMode.ByVoice)
        {
            int idx = Mathf.Abs(voiceIndex) % matches.Count;
            return matches[idx];
        }

        // RandomPerBin: independent random draw per bin, seeded from the session so it stays
        // consistent across every call site/re-application within one playthrough but varies
        // across separate playthroughs.
        if (matches[0].roleProfile != null && matches[0].roleProfile.configSelectionMode == RoleConfigSelectionMode.RandomPerBin)
        {
            var rng = SessionGenome.For($"{name}-{role}-bin{binIndex}-voice{voiceIndex}");
            return matches[rng.Next(matches.Count)];
        }

        // Default ByBin: deterministic round-robin so bin 0 → cfg[0], bin 1 → cfg[1], etc.
        int binIdx = Mathf.Abs(binIndex) % matches.Count;
        return matches[binIdx];
    }

    /// <summary>
    /// Returns distinct MusicalRoles present in roleNoteConfigs, in first-appearance order.
    /// The first role in this list is treated as primary (gets the largest Voronoi territory).
    /// </summary>
    public List<MusicalRole> GetActiveRoles()
    {
        var seen = new HashSet<MusicalRole>();
        var result = new List<MusicalRole>();
        for (int i = 0; i < roleNoteConfigs.Count; i++)
        {
            var cfg = roleNoteConfigs[i];
            if (cfg != null && cfg.role != MusicalRole.None && seen.Add(cfg.role))
                result.Add(cfg.role);
        }
        return result;
    }

    /// <summary>
    /// Returns distinct (role, voiceIndex) voices present in roleNoteConfigs, in first-appearance
    /// role order. A role only produces more than one voice (index &gt; 0) when it has multiple
    /// configs AND its profile's configSelectionMode is ByVoice — the same flag GetConfigForRoleAtBin
    /// and InstrumentTrackController.ResolveMotifRoleProfile already use to pin per-voice configs, so
    /// what the player hears (voice-panned chord identity) and what they'll see (maze territory) stay
    /// consistent. Every currently-shipped motif is ByBin, so this returns exactly GetActiveRoles()'s
    /// roles at voice 0 for all existing content.
    /// </summary>
    /// <summary>
    /// Base color of each active role (see GetActiveRoles()), in the same order, pulled from
    /// this motif's own roleNoteConfigs entries — not MusicalRoleProfileLibrary — so it always
    /// matches whatever profile this motif actually plays.
    /// </summary>
    public List<Color> GetActiveRoleColors()
    {
        var roles = GetActiveRoles();
        var colors = new List<Color>(roles.Count);
        foreach (var role in roles)
        {
            for (int i = 0; i < roleNoteConfigs.Count; i++)
            {
                var cfg = roleNoteConfigs[i];
                if (cfg != null && cfg.role == role && cfg.roleProfile != null)
                {
                    colors.Add(cfg.roleProfile.GetBaseColor());
                    break;
                }
            }
        }
        return colors;
    }

    public List<RoleVoiceKey> GetActiveVoices()
    {
        var configsByRole = new Dictionary<MusicalRole, List<RoleMotifNoteSetConfig>>();
        var roleOrder = new List<MusicalRole>();
        for (int i = 0; i < roleNoteConfigs.Count; i++)
        {
            var cfg = roleNoteConfigs[i];
            if (cfg == null || cfg.role == MusicalRole.None) continue;
            if (!configsByRole.TryGetValue(cfg.role, out var list))
            {
                list = new List<RoleMotifNoteSetConfig>();
                configsByRole[cfg.role] = list;
                roleOrder.Add(cfg.role);
            }
            list.Add(cfg);
        }

        var result = new List<RoleVoiceKey>();
        foreach (var role in roleOrder)
        {
            var configs = configsByRole[role];
            bool byVoice = configs.Count > 1 && configs[0].roleProfile != null
                && configs[0].roleProfile.configSelectionMode == RoleConfigSelectionMode.ByVoice;
            if (!byVoice) { result.Add(new RoleVoiceKey(role, 0)); continue; }
            for (int v = 0; v < configs.Count; v++) result.Add(new RoleVoiceKey(role, v));
        }
        return result;
    }

}
