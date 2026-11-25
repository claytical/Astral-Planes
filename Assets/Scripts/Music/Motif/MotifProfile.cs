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
    menuName = "Astral Planes/Music/Motif/Motif Profile")]
public class MotifProfile : ScriptableObject
{
    [Header("Identity")]
    public string motifId;                  // Stable identifier (e.g. "RockSteady_A")
    [Tooltip("Designer-friendly name shown in the phase/motif carousel.")]
    public string displayName;             // Designer-friendly label

    [Tooltip("Key root as a MIDI note (60 = C4). Riffs and chord progressions are transposed to this root.")]
    public int keyRootMidi = 60;   // default C4
    [Header("Drums")]
    [Tooltip("Entry loops: minimal / nearly silent. One will be chosen at motif start.")]
    public List<AudioClip> entryDrumLoops = new();

    [Tooltip("Intensity loops tagged with a target (depth, breadth, pressure) point. Selection picks whichever tag is nearest the live-computed axis values.")]
    public List<IntensityClipTag> intensityClipTags = new();

    [Range(0f, 2f), Tooltip("Per-motif drum level: multiplies AudioMixConfig.drumVolume for every drum loop of this motif (live and carousel preview). 1 = unchanged. The resulting AudioSource volume caps at 1.")]
    public float drumVolumeMultiplier = 1f;

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
    [Tooltip("Tempo of this motif (beats per minute).")]
    public float bpm = 120f;
    [Tooltip("Grid steps per drum loop (16 = sixteenth notes over one bar).")]
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

    [Range(0f, 1f), Tooltip("Fraction of ALL occupied maze cells — every generation mode (default Voronoi, single-voice, exact-count) — reserved as generic filler BEFORE role-territory assignment runs. 0 = no universal filler (default; existing motifs unaffected). Composes additively with exact-count mode's own per-voice leftover-becomes-filler math: cells reserved here are removed from the pool exact-count voices claim from, so an exact-count motif can end up with MORE total filler than this ratio alone implies. All filler is permanent once cleared (single filler type since 2026-11-17).")]
    public float genericFillRatio01 = 0f;


    [Header("Harmony")]
    [Tooltip("Chord progression used while this motif is active.")]
    public ChordProgressionProfile chordProgression;

    [Tooltip("If set, collecting all SuperNode shards triggers this progression at the next loop boundary.")]
    public ChordProgressionProfile alternateChordProgressionProfile;

    [Header("Per-role NoteSet configs")]
    [Tooltip("Per-role NoteSet generation configs for this motif.")]
    public List<RoleMotifNoteSetConfig> roleNoteConfigs = new List<RoleMotifNoteSetConfig>();

    
    [Tooltip("How many entries at the front of beatProfileSequence play once per motif, then are skipped on wrap.")]
    public int beatIntroCount = 0;

    [FormerlySerializedAs("driveBeatsFromEnergy")]
    [Tooltip("If true, drums are driven by live CosmicDust density through beatProfileSequence.")]
    public bool driveBeatsFromDensity = true;

    [Header("Vehicle Trap")]
    [Tooltip("If true, a ring or box of dust grows in around each vehicle as part of this motif's maze build (SceneFlowCoordinator trap callback → InjectTrapCells*), so it arrives with the maze rather than popping in afterward.")]
    public bool spawnVehicleTrap = false;
    [Tooltip("Shape of the dust trap grown around each vehicle when Spawn Vehicle Trap is on.")]
    public TrapShape trapShape = TrapShape.Circle;
    [Min(1), Tooltip("Ring radius in grid cells (circle) or half-side length (box).")]
    public int trapRadius = 3;
    [Tooltip("Hidden role of the trap dust: shown as that role's darkened (latent) color like any uncarved cell; carving reveals it.")]
    public MusicalRole trapRole = MusicalRole.None;

    private void OnValidate() {
        if (beatIntroCount < 0) beatIntroCount = 0;
        if (entryDrumLoops == null) entryDrumLoops = new List<AudioClip>();
        if (intensityClipTags == null) intensityClipTags = new List<IntensityClipTag>();
        if (bridgeSamples == null) bridgeSamples = new List<AudioClip>();
        if (beatIntroCount > entryDrumLoops.Count) beatIntroCount = entryDrumLoops.Count;
        ValidateTimingAgainstClips();
    }

    /// <summary>
    /// True when a clip's length is a whole-number ratio of the phrase in either direction:
    /// a short clip loops N times underneath one phrase (phrase / N), or a long clip spans N
    /// phrases (phrase × N; DrumTrack swaps land on clip boundaries, so it stays on the grid).
    /// Only a non-integer ratio makes audio-bound and note-duration timing disagree.
    /// <paramref name="expectedDesc"/> names the nearest valid length, for warnings.
    /// </summary>
    public static bool ClipFitsPhrase(float clipSec, float phraseSec, out string expectedDesc)
    {
        expectedDesc = null;
        if (clipSec <= 0f || phraseSec <= 0f) return true;
        bool longer = clipSec >= phraseSec;
        int n = Mathf.Max(1, Mathf.RoundToInt(longer ? clipSec / phraseSec : phraseSec / clipSec));
        float expectedSec = longer ? phraseSec * n : phraseSec / n;
        float tol = Mathf.Max(0.02f, expectedSec * 0.02f);
        if (Mathf.Abs(clipSec - expectedSec) <= tol) return true;
        expectedDesc = longer ? $"{n} phrases = {expectedSec:F3}s" : $"1/{n} phrase = {expectedSec:F3}s";
        return false;
    }

    // Warns when an authored clip's real length isn't a whole-number ratio of what
    // bpm/stepsPerLoop implies for the full phrase (see ClipFitsPhrase).
    private void ValidateTimingAgainstClips()
    {
        if (bpm <= 0f || stepsPerLoop <= 0) return;
        float impliedLoopSec = stepsPerLoop * (60f / bpm) / 4f; // 4 steps/quarter (16th-note grid)

        void Check(AudioClip clip, string label)
        {
            if (clip == null || clip.length <= 0f) return;
            if (!ClipFitsPhrase(clip.length, impliedLoopSec, out var expected))
                Debug.LogWarning($"[MotifProfile:{name}] bpm={bpm} stepsPerLoop={stepsPerLoop} implies phrase={impliedLoopSec:F3}s (nearest valid length: {expected}) but '{label}' clip '{clip.name}' is {clip.length:F3}s.");
        }

        foreach (var c in entryDrumLoops) Check(c, "entryDrumLoops");
        foreach (var t in intensityClipTags) Check(t.clip, "intensityClipTags");
    }

    /// <summary>
    /// Returns the config to use for a given role and bin index. Governs note/riff CONTENT only —
    /// for the track's frozen instrument identity (soundfont, color, register), use
    /// GetInstrumentProfileForVoice instead, which pins one profile for the whole motif even when
    /// this method pools content from several competing profiles bin to bin.
    ///
    /// One config  → same config for every bin (only harmonic shift varies between bins).
    /// N configs, ByBin        → configs[binIndex % N] so each bin gets a distinct variation in round-robin order.
    /// N configs, RandomPerBin → one config drawn independently at random per bin, seeded from
    ///                           SessionGenome so the draw is stable for the whole play session
    ///                           but varies across separate playthroughs.
    ///
    /// Both modes are deterministic given (session, role, binIndex, voiceIndex): repeated calls with
    /// the same arguments always return the same config, which is essential for saturation detection
    /// and for DiscoveryTrackNode re-spawns being consistent with what was originally placed.
    /// </summary>
    public RoleMotifNoteSetConfig GetConfigForRoleAtBin(MusicalRole role, int binIndex, int totalBins, int voiceIndex = 0)
    {
        var matches = GatherMatches(role);
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

    private List<RoleMotifNoteSetConfig> GatherMatches(MusicalRole role)
    {
        if (roleNoteConfigs == null || roleNoteConfigs.Count == 0) return null;
        List<RoleMotifNoteSetConfig> matches = null;
        for (int i = 0; i < roleNoteConfigs.Count; i++)
        {
            var cfg = roleNoteConfigs[i];
            if (cfg != null && cfg.role == role)
            {
                matches ??= new List<RoleMotifNoteSetConfig>();
                matches.Add(cfg);
            }
        }
        return matches;
    }

    /// <summary>
    /// Returns the single MusicalRoleProfile that plays this (role, voiceIndex) for the whole
    /// motif — the track's frozen instrument identity (soundfont, mixer routing, register,
    /// dust/visual color). Deliberately independent of GetConfigForRoleAtBin, which keeps
    /// pooling and drawing note/riff CONTENT from every competing config every bin regardless
    /// of which profile wins here.
    ///
    /// - Single distinct profile backing this (role, voiceIndex): returned directly, no randomness.
    /// - ByVoice mode: unaffected by winner selection — voice index maps 1:1 to its own config's
    ///   profile, same as GetConfigForRoleAtBin.
    /// - Multiple DISTINCT MusicalRoleProfile assets competing under ByBin/RandomPerBin: one is
    ///   picked ONCE per motif with EQUAL weight per distinct profile (not weighted by how many
    ///   configs/riffs a profile happens to author), via SessionGenome scoped WITHOUT binIndex so
    ///   the pick is stable for the motif's duration but varies session-to-session.
    /// </summary>
    public MusicalRoleProfile GetInstrumentProfileForVoice(MusicalRole role, int voiceIndex = 0)
    {
        var matches = GatherMatches(role);
        if (matches == null || matches.Count == 0) return null;
        if (matches.Count == 1) return matches[0].roleProfile;

        if (matches[0].roleProfile != null && matches[0].roleProfile.configSelectionMode == RoleConfigSelectionMode.ByVoice)
        {
            int idx = Mathf.Abs(voiceIndex) % matches.Count;
            return matches[idx].roleProfile;
        }

        // Dedup by distinct profile IDENTITY (not by config count) so authoring more riffs under
        // one profile doesn't skew its odds of winning the instrument slot.
        var distinctProfiles = new List<MusicalRoleProfile>();
        foreach (var cfg in matches)
        {
            if (cfg.roleProfile != null && !distinctProfiles.Contains(cfg.roleProfile))
                distinctProfiles.Add(cfg.roleProfile);
        }
        if (distinctProfiles.Count <= 1)
            return distinctProfiles.Count == 1 ? distinctProfiles[0] : matches[0].roleProfile;

        // Scope excludes binIndex on purpose: one stable answer for the whole motif.
        var rng = SessionGenome.For($"{name}-{role}-voice{voiceIndex}-instrument");
        return distinctProfiles[rng.Next(distinctProfiles.Count)];
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
