using UnityEngine;
using UnityEngine.Serialization;

// Each ring built by MotifRingGlyphApplicator has two visual layers: a "Guide Circle" (the
// filled annulus, "Fill" in code) and a "Notation Line" (the LineRenderer contour that dips
// inward at each note, "Contour" in code). Headers below group fields by which layer they
// affect — Fill-only, Contour-only, or Shared (both layers, or the whole ring as a unit) —
// plus a few headers for visual elements that are neither layer (background discs, the note
// travel dot, the tether).
//
// Gameplay-only and track-selection-only fields are collapsed into foldout drawers by
// Assets/Editor/RingGlyphConfigEditor.cs (its name lists must be updated when adding such a field;
// unlisted fields appear in the shared section).
[CreateAssetMenu(fileName = "RingGlyphConfig", menuName = "Astral Planes/Rings & Records/Ring Glyph Config")]
public class RingGlyphConfig : ScriptableObject
{
    [Header("Layout (Shared)")]
    [Tooltip("Size of the hole at the centre: inner edge radius of the innermost ring, in local units. " +
             "Honored literally by the carousel's selected glyph and by gameplay rings (which are then " +
             "scaled to the play area). Ignored by carousel unselected glyphs, which are fixed-fit.")]
    public float innerRadius = 0.08f;

    [Tooltip("Radial thickness of each filled ring in local units. Ignored by carousel unselected " +
             "glyphs (fixed-fit picks its own thickness).")]
    public float ringThickness = 0.04f;

    [Tooltip("Gap between the outer edge of one ring and the inner edge of the next. In fixed-fit " +
             "(carousel unselected glyphs) only the ratio interBinSpacing / ringSpacing matters. For gameplay and " +
             "locked-preview rings this is the uniform gap everywhere. For record/carousel rings " +
             "(AnimateApply/ApplyVinyl) it means something narrower: the gap WITHIN a bin's " +
             "band, between rings that share the same BinIndex — keep it small. See interBinSpacing " +
             "below for the gap BETWEEN one bin's band and the next.")]
    public float ringSpacing = 0.02f;

    [Tooltip("Record/carousel rings only (AnimateApply/ApplyVinyl) — gap between one bin's " +
             "ring band and the next, distinct from ringSpacing's now-narrower within-band meaning. " +
             "Should be noticeably larger than ringSpacing, and tuned large enough relative to " +
             "tugDepthFraction that one band's note dips don't visually reach into the next band. " +
             "Gameplay and locked-preview rings ignore this entirely — they still use ringSpacing " +
             "uniformly via RingInnerRadius.")]
    public float interBinSpacing = 0.1f;

    [Range(64, 512)]
    [Tooltip("Number of vertices per ring. Higher values = smoother notch curves. Feeds the Fill mesh's " +
             "triangle count, the Contour's point count, and the background discs' mesh resolution.")]
    public int segments = 256;

    [Tooltip("Radians added to every ring-angle calculation (contour points, note dips, travel-dot/" +
             "tether landing spots, the note-fired firework, and the draw-in sweep's progress angle), " +
             "rotating where angle=0 sits for the WHOLE stack — purely cosmetic (which point the " +
             "draw-in sweep visibly starts from; 0 is 3 o'clock, Mathf.PI/2 the default is 12 " +
             "o'clock). Every angle formula in the ring-glyph pipeline adds this same base offset, " +
             "so changing it rotates the whole system together. Each ring ALSO gets its own extra " +
             "stagger on top of this shared base — see MotifRingGlyphGenerator.RingOrdinalAngleJitter " +
             "— since the Contour's coincident start/end point (BuildRingPoints duplicates the first " +
             "point to close the ring; see MotifRingGlyphApplicator.RingBuilder.cs for why loop=true " +
             "isn't used instead) leaves a faint per-ring seam kink, and every ring in a stack sharing " +
             "this one base offset would otherwise line their kinks up into one visible radial line.")]
    public float ringStartAngleOffset = Mathf.PI / 2f;

    [Range(0f, 0.4f)]
    [Tooltip("Fraction of the play area height reserved as padding on each side. " +
             "0.05 = 5% padding, so the outermost ring fills 90% of the play area height.")]
    public float fitPaddingFraction = 0.05f;

    [Header("Guide Circle — Fill")]
    [Tooltip("Material for filled annulus rings. Must support alpha blending. " +
             "Assign a URP Unlit/Transparent or Sprites/Default material. Also applied to the two " +
             "background backdrop discs (CreateBackgroundDisc) — not just the ring fills.")]
    public Material ringMeshMaterial;

    [Range(0f, 1f)]
    [Tooltip("Base alpha applied to all filled rings. Low values allow rings to stack " +
             "on top of each other without fully obscuring what is behind.")]
    public float ringAlpha = 0.45f;

    [Header("Notation Line — Contour")]
    [Range(0f, 1f)]
    [Tooltip("Alpha for the contour LineRenderer. 1 = fully opaque track color.")]
    public float contourAlpha = 1f;

    [Tooltip("LineRenderer width in local units. In-game record: also the width of the placeholder " +
             "(not-yet-recorded) rings, so committed and placeholder rings share one line weight.")]
    public float lineWidth = 0.003f;

    [Range(0f, 1f)]
    [Tooltip("In-game record only: every ring's line is capped at this fraction of the INNERMOST ring's outer " +
             "radius (its note dips are the tightest), so the line can't fold over itself there and all rings " +
             "share one width. 0 = no cap.")]
    public float maxLineWidthToRadius = 0.25f;

    [Tooltip("Contour width for record/carousel rings (AnimateApply/ApplyVinyl) only — wider than lineWidth so the ethereal edge-softness gradient (RingLineEthereal.mat) is actually visible instead of collapsing into a few pixels. Gameplay rings use lineWidth; placeholder rings use remainingRingLineWidth.")]
    public float recordLineWidth = 0.012f;

    [Range(0f, 1f)]
    [Tooltip("Contour notch depth at a note, as a fraction of that ring's own radius (outer rings dip farther in " +
             "absolute terms). 1 reaches the exact center of the glyph. Also sets where travel dots, tethers and " +
             "fireworks land (outerRadius × (1 − depth)). Where notes overlap the deepest one wins — depths don't add. " +
             "In the carousel's selected glyph the notch shows only waveRestDepth of this between notes (negative = bulged outward). Baked when a " +
             "glyph is built. Subtle ≈ 0.2, pronounced ≈ 0.65.")]
    public float tugDepthFraction = 0.65f;

    [Range(0.02f, Mathf.PI)]
    [Tooltip("Angular half-width of each note's notch, in radians (0.8 ≈ 46°, ~92° total). Scaled 0.7×–1.3× per note " +
             "by its commit time (fast = narrower, slow = wider). Neighboring steps are only ~0.2–0.4 rad apart, so " +
             "values much above 0.3 make adjacent notches merge into one plateau. π makes every note's notch span " +
             "the whole ring. Baked when a glyph is built.")]
    public float tugHalfWidthRad = 0.8f;

    [Header("Playback Highlight (Fill + Contour)")]
    [Tooltip("Scale multiplier applied to a ring while its bin is the one currently playing in RingPreviewPlayer.")]
    public float highlightScaleMultiplier = 1.15f;

    [Range(0f, 1f)]
    [Tooltip("How far to lerp a highlighted ring's Fill/Contour color toward white. 0 = no brightening, 1 = fully white.")]
    public float highlightBrightness = 0.5f;

    [Tooltip("Selected glyph: the playing bin's rings slide to the outermost slots and the other bands " +
             "shift inward (same order), returning to the recorded order when playback stops.")]
    public bool playingBinToOuter = true;

    [Min(0f)]
    [Tooltip("Seconds for the bands to slide to their new slots when the playing bin changes.")]
    public float binSwapDuration = 0.5f;

    [Tooltip("Seconds to lerp a ring's scale and color in when its playing-bin highlight turns on.")]
    public float highlightPulseDuration = 0.15f;

    [Tooltip("Seconds to lerp a ring's scale and color back down when its playing-bin highlight turns " +
             "off. Kept separate from highlightPulseDuration so the outgoing ring can be made to clear " +
             "away faster than the incoming one brightens — set low (or 0 for an instant snap) if " +
             "overlapping fade windows are reading as a lingering 'other bin' ring during playback.")]
    public float nonHighlightedTransitionDuration = 0.15f;

    [Range(0f, 1f)]
    [Tooltip("How far to darken a NON-highlighted ring's Fill/Contour color toward black while another bin is playing. 0 = no darkening, 1 = fully black.")]
    public float nonHighlightedDarkenAmount = 0.6f;

    [Range(0f, 1f)]
    [Tooltip("Multiplier on a NON-highlighted ring's normal alpha (ringAlpha/contourAlpha) while another bin is playing — lower = more transparent.")]
    public float nonHighlightedAlphaMultiplier = 0.35f;

    [Header("Remaining / Locked Placeholder Rings")]
    [Tooltip("Fill/contour color for gray placeholder rings representing motif progress not yet completed.")]
    public Color remainingRingColor = new Color(0.6f, 0.6f, 0.6f, 1f);

    [Range(0f, 1f)]
    [Tooltip("Fill alpha for remaining-progress placeholder rings. These rings never draw a Contour " +
             "(see RecordRings.cs's ApplyLockedPreview / GameplayRings.cs's SetRemainingProgressRings — " +
             "a placeholder never has notes, so there's nothing for the Contour to trace, and skipping " +
             "it avoids a fragile LineRenderer closure entirely), so this Fill alpha is what the whole " +
             "ring's visibility depends on — keep it clearly visible, not just a faint wash.")]
    public float remainingRingAlpha = 0.4f;

    [Min(0f)]
    [Tooltip("Width of the remaining/locked placeholder rings. They are filled meshes with no Contour, so " +
             "the fill band is thinned to this width, centred on the ring's outer edge. 0 = use the full " +
             "ringThickness. Applies to gameplay remaining rings and carousel locked-preview rings.")]
    public float remainingRingLineWidth = 0.012f;

    [Tooltip("Optional material for placeholder ring fills (e.g. RingLineEthereal.mat for the soft-edged " +
             "carousel look). Falls back to ringMeshMaterial when null.")]
    public Material remainingRingMaterial;

    [Tooltip("Seconds to lerp from one active role color to the next on the next-up placeholder ring.")]
    public float nextUpColorCycleDuration = 1.5f;

    [Header("Draw-In / Reveal Animation (Shared)")]
    [Tooltip("Seconds for each ring to trace itself in — both the Fill mesh's triangle reveal and the " +
             "Contour's point-count reveal, in lockstep. Used everywhere a ring draws itself in: the " +
             "main record's rings, locked-preview placeholders, freshly-spawned gameplay bin rings, " +
             "and remaining-progress placeholders.")]
    public float ringDrawInDuration = 0.5f;

    [Tooltip("Seconds between the start of successive ring draw-ins.")]
    public float ringStaggerDelay = 0.08f;

    [Header("Rotation (Shared)")]
    [Tooltip("Record/carousel rings only — rotation speed (deg/sec) at a 0 density+match-ratio score " +
             "(see rotSpeedMax for the score-1 end).")]
    public float rotSpeedBase = 20f;

    [Tooltip("Maximum rotation speed cap in degrees/sec.")]
    public float rotSpeedMax = 300f;

    [Range(0f, 1f)]
    [Tooltip("Record/carousel rings only — a ring's MatchRatio must meet this to spin in the " +
             "default (positive) direction; below it, the ring spins reversed. Distinguishes " +
             "clean/matched bins from improvised/unmatched-heavy ones.")]
    public float directionMatchRatioThreshold = 0.5f;

    [Header("Note Pulse (Track Selection)")]
    [Range(-1f, 1f)]
    [Tooltip("Track selection's sounding ring only — fraction of a note's full dip depth while the note " +
             "isn't sounding. 0 = dips flatten between notes, 1 = no pulse, negative = the note rests as an " +
             "outward bump of that fraction of the dip depth and each hit swings it through to full depth.")]
    public float waveRestDepth = -0.4f;

    [Min(0.001f)]
    [Tooltip("Seconds for a dip to swell from rest to full depth when its note starts.")]
    public float notePulseAttackSec = 0.03f;

    [Min(0f)]
    [Tooltip("Shortest time a pulse takes to relax back to rest, so very short notes still read.")]
    public float notePulseMinSec = 0.12f;

    [Tooltip("Seconds to delay the pulse to line up with the synth's audible output latency. " +
             "Raise it if the line moves before you hear the note.")]
    public float notePulseLatencySec = 0f;

    [Tooltip("Track selection's sounding ring only — when a note fires, turn its ring so that note's " +
             "dip sits at the top. The ring turns clockwise continuously, so each note is at 12 as it pulses and the ring keeps moving toward the next.")]
    public bool notePulseRotateToTop = true;

    [Range(0f, 360f)]
    [Tooltip("Where on the glyph the sounding note sits as it pulses, in degrees counter-clockwise from " +
             "3 o'clock: 90 = 12 o'clock, 180 = 9, 270 = 6 o'clock. The ring still turns clockwise.")]
    public float notePulseCueDeg = 180f;

    [Header("Note Travel Dot & SFX")]
    [Tooltip("Radius of the mini-ring dot that travels from note marker to tug point (in ring-local units).")]
    public float noteDotRadius = 0.02f;

    [Tooltip("Duration in seconds for each note-to-tug travel animation.")]
    public float noteTravelDuration = 0.35f;

    [Tooltip("Percussive AudioClip played when a note travel dot launches.")]
    public AudioClip launchSfx;

    [Range(0f, 1f)]
    [Tooltip("Volume for the launch SFX.")]
    public float launchSfxVolume = 0.6f;

    [Tooltip("Percussive AudioClip played when a note travel dot impacts the ring surface.")]
    public AudioClip impactSfx;

    [Range(0f, 1f)]
    [Tooltip("Volume for the impact SFX.")]
    public float impactSfxVolume = 0.6f;

    [Header("Note Tether")]
    [FormerlySerializedAs("noteTetherGrowDuration")]
    [Tooltip("Seconds for a note's preview tether to animate — both growing in from the timeline marker " +
             "to its ring landing spot, and retracting/detaching when broken or departing.")]
    public float noteTetherTransitionDuration = 0.25f;

    [Header("Background Discs")]
    [Tooltip("Color for the two solid backdrop discs behind the ring stack (alpha is set separately per disc).")]
    public Color backgroundDiscColor = Color.black;

    [Range(0f, 1f)]
    [Tooltip("Alpha for the inner backdrop disc, sized to rings already placed. More opaque than the full-record disc.")]
    public float placedBackgroundAlpha = 0.35f;

    [Range(0f, 1f)]
    [Tooltip("Alpha for the outer backdrop disc, sized to the entire record (placed + remaining). Fainter than the placed disc.")]
    public float fullBackgroundAlpha = 0.15f;

    // Three sequential phases of one gameplay-ring exit sequence: spin, then tilt, then scale to
    // zero. Read only externally, via BridgeCoordinator.cs → SpinAndRollOffActiveRings (SpinOff.cs)
    // — nothing in this file's own consumers (the MotifRingGlyphApplicator partials) reads these
    // four fields directly.
    [Header("Gameplay Ring Exit (Spin → Tilt → Scale)")]
    [Tooltip("Phase 1 (360° spin) duration of the gameplay ring exit sequence, immediately followed by " +
             "rollOffDuration's tilt phase — the two run back-to-back, not as alternatives.")]
    public float spinOffDuration = 0.45f;

    [Tooltip("Phase 2 (tilt) duration of the gameplay ring exit sequence — lerps the ring to tiltXDegrees " +
             "before it scales to zero.")]
    public float rollOffDuration = 1.5f;

    [Tooltip("Target X-axis rotation (degrees) for the tilt-exit phase.")]
    public float tiltXDegrees = 75f;

    [Tooltip("Phase 3: duration (seconds) for the ring to scale from its current size to zero after the tilt.")]
    public float scaleDownDuration = 0.5f;

    [Header("Ring Collision Feedback")]
    [Tooltip("Seconds the ring shakes for after successfully breaking a pending tether.")]
    public float breakShakeDuration = 0.35f;

    [Tooltip("Peak position offset (local units) for the break shake.")]
    public float breakShakeAmplitude = 0.18f;

    [Tooltip("Perlin-noise sample frequency for the break shake — lower reads as a looser, springier wobble.")]
    public float breakShakeFrequency = 18f;

    [Tooltip("Seconds the ring shakes for on a denied collision (no pending tether to break).")]
    public float denialShakeDuration = 0.14f;

    [Tooltip("Peak position offset (local units) for the denial shake.")]
    public float denialShakeAmplitude = 0.07f;

    [Tooltip("Perlin-noise sample frequency for the denial shake — higher reads as a stiff, clipped jitter.")]
    public float denialShakeFrequency = 40f;

    // Both fields here are consumed externally, one layer removed from this file's own code:
    // recordHoldDuration only via BinRingController.GetRecordHoldDuration() → SceneFlowCoordinator;
    // recordFadeOutDuration via FadeRecordOut (GameplayRings.cs).
    [Header("Ring Complete Flare")]
    [Tooltip("When a gameplay ring's last note lands: particle burst around the ring, a few pulses, " +
             "and a brightness wave in its colour rippling outward through the remaining placeholder rings.")]
    public bool enableRingCompleteFlare = true;

    [Tooltip("Burst prefabs spawned evenly around the completed ring.")]
    [Min(1)] public int ringCompleteBurstPoints = 12;

    [Tooltip("Peak scale of each pulse of the completed ring (1 = no swell).")]
    public float ringCompletePulseScale = 1.08f;

    [Tooltip("Number of swells over ringCompletePulseDuration.")]
    [Min(1)] public int ringCompletePulseCount = 2;

    [Tooltip("Seconds for all of the completed ring's pulses together.")]
    public float ringCompletePulseDuration = 0.9f;

    [Tooltip("Seconds between each placeholder ring lighting up, innermost first (the ripple's speed).")]
    public float ringCompleteRippleStepDelay = 0.08f;

    [Tooltip("Seconds for one placeholder ring to brighten to the flare colour.")]
    public float ringCompleteRippleRise = 0.12f;

    [Tooltip("Seconds for one placeholder ring to fade back to its normal look.")]
    public float ringCompleteRippleFall = 0.6f;

    [Tooltip("Fill alpha of a placeholder ring at the peak of the ripple.")]
    [Range(0f, 1f)] public float ringCompleteFlareAlpha = 1f;

    [Header("Record Hold / Fade Out")]
    [Tooltip("Seconds the completed record stays fully visible (100% opacity) before it starts " +
             "fading out, once shown at the start of a new motif.")]
    public float recordHoldDuration = 1.5f;

    [Tooltip("Default seconds for the initial record to fade out when not synced to maze generation " +
             "(ordinary/path-choice motif transitions, where the new maze is already fully generated " +
             "by the time the record reappears).")]
    public float recordFadeOutDuration = 0.75f;

    // Record recovery (MotifRingGlyphApplicator.Dock.cs): when the motif completes, the finished
    // record is shown full-size, then shrinks and moves to its dock spot to be collected.
    [Header("Record Dock (collection)")]
    [Tooltip("Fallback for the next-up placeholder ring's colour cycle when no role colours are supplied " +
             "(SetNextUpPalette): it pulses between remainingRingColor and this.")]
    public Color nextUpHighlightColor = new Color(0.95f, 0.95f, 1f, 1f);

    [Range(0f, 1f)]
    [Tooltip("Opacity of the next-up placeholder ring (the one cycling role colours). Separate from " +
             "remainingRingAlpha so the cycle can read at full strength while the other placeholders stay faint.")]
    public float nextUpRingAlpha = 1f;

    [Tooltip("Show the next-up ring's role colours at full brightness (hue and saturation kept, value pushed to 1), " +
             "so each role reads clearly even when its profile colour is dark.")]
    public bool nextUpMaximizeBrightness = true;

    [Tooltip("Seconds per strain pulse of the next-up placeholder ring (it swells outward, as if trying to get out, then settles).")]
    public float nextUpStrainPeriod = 1.2f;

    [Tooltip("Peak scale of the next-up placeholder ring's strain pulse (1 = none).")]
    public float nextUpStrainScale = 1.06f;

    [Header("Record Dock (trapped drift)")]
    [Tooltip("Speed of the docked record's slow wander inside its chamber.")]
    public float dockDriftWanderSpeed = 0.35f;

    [Tooltip("Fraction of the free room in the chamber the slow wander uses (lunges use all of it).")]
    [Range(0f, 1f)] public float dockDriftWanderShare = 0.45f;

    [Tooltip("Seconds between the trapped record's lunges at its shell (random in this range).")]
    public Vector2 dockLungeIntervalRange = new Vector2(2.5f, 5f);

    [Tooltip("Seconds one lunge takes: a quick push to the shell, then a spring back.")]
    public float dockLungeSeconds = 0.7f;

    [Tooltip("Seconds the finished record stays shown full-size before it shrinks to its dock spot.")]
    public float dockHoldDuration = 1.0f;

    [Tooltip("Seconds the finished record takes to shrink and move to its dock spot.")]
    public float dockMoveDuration = 1.2f;
}
