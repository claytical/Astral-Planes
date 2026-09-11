using UnityEngine;

[CreateAssetMenu(fileName = "CosmicDustGeneratorConfig", menuName = "Astral Planes/Dust/Cosmic Dust Generator Config")]
public class CosmicDustGeneratorConfig : ScriptableObject
{
    [Header("Grid & Collision")]
    [Tooltip("World-units clearance inside each cell. 0 = watertight.")]
    public float cellClearanceWorld = 0f;
    [Tooltip("Sprite footprint relative to one grid cell. >1.0 = overlap with neighbours, blending the grid into a dustier cloud.")]
    [Range(0.8f, 1.6f)] public float dustFootprintMul = 1.4f;

    [Header("Spawn")]
    [Tooltip("Frame budget for initial maze spawn (milliseconds). Tune for target hardware.")]
    public float maxSpawnMillisPerFrame = 1.2f;
    [Tooltip("Visual grow-in duration per cell when spawning a maze hex.")]
    public float hexGrowInSeconds = 0.45f;

    [Header("Regrowth")]
    [Tooltip("Max cells that transition from PendingRegrow to Solid per drum step.")]
    public int regrowCellsPerStep = 1;
    [Tooltip("Seconds after a cell becomes visible before its collider re-enables.")]
    public float regrowColliderEnableDelaySeconds = 0.20f;

    [Header("Regrow Veto (Vehicle Overlap)")]
    [Tooltip("Delay before retrying a vetoed regrow cell.")]
    public float regrowVetoRetryDelaySeconds = 0.5f;
    [Tooltip("Overlap box size as a fraction of cell world size.")]
    [Range(0.25f, 1.25f)] public float regrowVetoBoxMul = 0.85f;
    public int regrowVetoMaxHits = 8;

    [Header("Void Grow")]
    [Tooltip("Sprite scale-in and tint fade duration for gravity void dust.")]
    [Range(0.1f, 2f)] public float voidDustGrowInSeconds = 0.40f;
    [Tooltip("Fraction of a ring burst's growInSeconds spent rippling outward from the inner edge before each cell starts its own grow-in tween. 0 = old synchronous pop.")]
    [Range(0f, 0.6f)] public float voidRingRippleFraction = 0.35f;

    [Header("Zap Clear")]
    [Tooltip("Visual fade duration when a star zap clears a dust cell.")]
    public float zapFadeSeconds = 1.5f;
    [Tooltip("Explicit override for zapped-cell regrow delay. -1 defers to the maze pattern's zapRegrowDelay / base regrowDelay.")]
    public float zapRegrowDelaySeconds = -1f;
    [Tooltip("Sprite scale-out duration when a star drain clears a cell. Shorter = punchier.")]
    [Min(0.01f)] public float zapScaleOutSeconds = 0.12f;
    
    [Header("Generic Fill Resistance")]
    [Range(0f, 1f), Tooltip("Baseline carve resistance for MusicalRole.None dust, baked into the " +
        "imprint at spawn/regrow time instead of the previous hardcoded 0. Default 0 reproduces " +
        "current behavior (instant carve).")]
    public float noneCarveResistance01 = 0f;
    [Range(0f, 1f), Tooltip("Baseline drain resistance for MusicalRole.None dust. Default 0 reproduces current behavior.")]
    public float noneDrainResistance01 = 0f;
    [Min(1), Tooltip("Baseline max energy units for MusicalRole.None dust. Default 1 reproduces current behavior.")]
    public int noneMaxEnergyUnits = 1;

    [Header("Visual")]
    public Color mazeTint = new Color(0.7f, 0.7f, 0.7f, 0.25f);
    [Tooltip("Distinct darker gray for cells deliberately filled with permanent generic MusicalRole.None dust (exact-count mode's leftover territory) — visually distinguishes 'permanent filler, never regrows this motif' cells from ordinary quick-regrowing gray dust (mazeTint), from the moment the maze spawns.")]
    public Color genericFillTint = new Color(0.35f, 0.35f, 0.35f, 0.25f);
    [Tooltip("Color for the bypass-proof 'Solid' dust type (CellFlags.Solid) — uncarveable " +
             "walls used by the path-choice interstitial maze and its resealed losing tunnels. " +
             "Deliberately dim/desaturated against mazeTint so it reads as impassable. Kept off " +
             "near-black on purpose: dust is particle-only now, and a near-black startColor read " +
             "as fully invisible under the particle material's blend mode in an earlier attempt.")]
    public Color solidDustTint = new Color(0.28f, 0.25f, 0.32f, 1f);

    [Tooltip("Cosmetic only — never affects Role/targeting/star-arming, only pre-carve display color. " +
             "When false (default), a motif's dust shows a darkened preview of its secret role color " +
             "immediately instead of flat mazeTint gray; carving still reveals the full color as today. " +
             "When true, restores the original fully-hidden flat-gray look.")]
    public bool hideDustColorAtSpawn = false;
    [Range(0f, 1f), Tooltip("How far the darkened spawn-time preview color blends toward black. " +
             "Only used when hideDustColorAtSpawn is false.")]
    public float hiddenRoleColorDarken01 = 0.55f;

    [Header("Topology")]
    [Tooltip("When enabled, the dust grid wraps toroidally — cells at one edge connect to the opposite edge.")]
    public bool toroidal = false;

    [Header("Motif Density Normalization")]
    [Tooltip("Average notes-per-bin per active voice at/below which a motif reads as fully sparse (busyness01=0). Reference scale, not per-motif authoring.")]
    public float minNotesPerBinPerVoice = 2f;
    [Tooltip("Average notes-per-bin per active voice at/above which a motif's busyness01 saturates to 1.")]
    public float maxNotesPerBinPerVoice = 16f;
    [Tooltip("Motif bpm at/below which drum-tempo busyness reads as 0. Reference scale, not per-motif authoring.")]
    public float minBpmForDensity = 80f;
    [Tooltip("Motif bpm at/above which drum-tempo busyness saturates to 1.")]
    public float maxBpmForDensity = 160f;
    [Tooltip("Weight of note-density busyness in the final MotifBusyness01 blend. Paired with bpmDensityWeight; both normalize internally so they need not sum to 1.")]
    public float noteDensityWeight = 1f;
    [Tooltip("Weight of drum-tempo (MotifProfile.bpm) busyness in the final MotifBusyness01 blend. 0 = current behavior (note-density only).")]
    public float bpmDensityWeight = 0f;

    [Header("Tint Diffusion")]
    [Tooltip("If enabled, recently modified cells gradually blend toward their neighbours.")]
    public bool enableTintDiffusion = true;
    [Tooltip("Seconds between diffusion passes. Lower = smoother but more CPU.")]
    [Range(0.02f, 0.5f)] public float tintDiffusionInterval = 0.12f;
    [Tooltip("Max cells processed per diffusion pass.")]
    [Range(16, 2048)] public int tintDiffusionMaxCellsPerTick = 256;
    [Tooltip("Neighbourhood radius for diffusion averaging (1 = 8-neighbourhood).")]
    [Range(0, 3)] public int tintDiffusionRadius = 1;
    [Tooltip("How strongly each pass nudges a cell toward the neighbourhood average.")]
    [Range(0f, 1f)] public float tintDiffusionStrength = 0.12f;
    [Tooltip("When a cell changes materially due to diffusion, enqueue its immediate neighbours.")]
    public bool tintDiffusionPropagateOnChange = true;
    [Tooltip("Minimum per-channel delta required to apply a diffusion step.")]
    [Range(0f, 0.05f)] public float tintDiffusionMinDelta = 0.0025f;
    [Tooltip("How far out to mark cells dirty when a tint-affecting event occurs.")]
    [Range(0, 3)] public int tintDirtyMarkRadius = 1;

    [Header("Regrow Speed Preview")]
    [Tooltip("Pulse each regrown cell toward a fast/slow color on commit, so regrow speed reads as a learnable color cue instead of only being discoverable through repeated play.")]
    public bool regrowSpeedPreviewEnabled = true;
    [Tooltip("MusicalRoleProfile.regrowthDelay value that maps to the fully-fast preview color.")]
    [Min(0f)] public float regrowSpeedPreviewFastSeconds = 0.5f;
    [Tooltip("MusicalRoleProfile.regrowthDelay value that maps to the fully-slow preview color.")]
    [Min(0f)] public float regrowSpeedPreviewSlowSeconds = 2.5f;
    [Tooltip("Color pulsed toward for roles at or below regrowSpeedPreviewFastSeconds.")]
    public Color regrowSpeedPreviewFastColor = new Color(1f, 0.55f, 0.15f);
    [Tooltip("Color pulsed toward for roles at or above regrowSpeedPreviewSlowSeconds.")]
    public Color regrowSpeedPreviewSlowColor = new Color(0.3f, 0.85f, 1f);
    [Tooltip("Duration of the pulse fired on regrow commit. Keep this well under regrowColliderEnableDelaySeconds " +
             "(0.2s default) — EnsureMinSolidAlpha fires at that mark and boosts the cell to full opacity while " +
             "the pulse coroutine is still tracking live alpha, so a pulse that outlasts that delay ends up held " +
             "fully opaque for its remaining duration instead of fading out quickly.")]
    [Min(0.01f)] public float regrowSpeedPreviewPulseSeconds = 0.15f;
    [Tooltip("Blends the pulse color toward white by this fraction. Keep small — held at full opacity past " +
             "regrowColliderEnableDelaySeconds, even a moderate boost reads as a wash of white rather than a tint.")]
    [Range(0f, 1f)] public float regrowSpeedPreviewGlowBoost01 = 0.05f;

    [Header("Regrow Density Easing")]
    [Tooltip("Shared with GravityVoidController.ApplyDensityThrottle — solid fraction at which density pressure starts easing (regrow slows, new void-growth rings halve).")]
    [Range(0f, 1f)] public float densityThrottleHalfAt = 0.5f;
    [Tooltip("Shared with GravityVoidController.ApplyDensityThrottle — solid fraction at which easing is at its strongest (regrow at its slowest, new void-growth rings skipped).")]
    [Range(0f, 1f)] public float densityThrottleSkipAt = 0.8f;
    [Tooltip("Regrow delay multiplier applied once solid fraction reaches densityThrottleSkipAt. Ramps smoothly from 1x at densityThrottleHalfAt. 1 = no easing.")]
    [Min(1f)] public float regrowDensityEaseMaxMultiplier = 2.5f;
}
