using UnityEngine;

[CreateAssetMenu(fileName = "RingGlyphConfig", menuName = "Astral Planes/Ring Glyph Config")]
public class RingGlyphConfig : ScriptableObject
{
    [Header("Layout")]
    [Tooltip("Inner edge radius of the innermost ring in local units (parent-relative).")]
    public float innerRadius = 0.08f;

    [Tooltip("Radial thickness of each filled ring in local units.")]
    public float ringThickness = 0.04f;

    [Tooltip("Gap between the outer edge of one ring and the inner edge of the next.")]
    public float ringSpacing = 0.02f;

    [Range(0f, 0.4f)]
    [Tooltip("Fraction of the play area height reserved as padding on each side. " +
             "0.05 = 5% padding, so the outermost ring fills 90% of the play area height.")]
    public float fitPaddingFraction = 0.05f;

    [Header("Filled Ring Appearance")]
    [Tooltip("Material for filled annulus rings. Must support alpha blending. " +
             "Assign a URP Unlit/Transparent or Sprites/Default material.")]
    public Material ringMeshMaterial;

    [Range(0f, 1f)]
    [Tooltip("Base alpha applied to all filled rings. Low values allow rings to stack " +
             "on top of each other without fully obscuring what is behind.")]
    public float ringAlpha = 0.45f;

    [Range(0f, 1f)]
    [Tooltip("Alpha for the contour LineRenderer. 1 = fully opaque track color.")]
    public float contourAlpha = 1f;

    [Header("Line")]
    [Tooltip("LineRenderer width in local units.")]
    public float lineWidth = 0.003f;

    [Range(64, 512)]
    [Tooltip("Number of vertices per ring. Higher values = smoother sine dips.")]
    public int segments = 256;

    [Header("Tug (inward dip per note)")]
    [Range(0f, 1f)]
    [Tooltip("Inward dip depth as a fraction of the ring radius. 0.65 gives a pronounced pac-man dip; tune in Inspector (0.4 = subtle, 0.85 = very deep).")]
    public float tugDepthFraction = 0.65f;

    [Tooltip("Angular half-width of each tug bell in radians (0.8 ≈ 46°, giving ~92° total mouth width per note).")]
    public float tugHalfWidthRad = 0.8f;

    [Header("Animation")]
    [Tooltip("Seconds for each ring to trace itself in.")]
    public float ringDrawInDuration = 0.5f;

    [Tooltip("Seconds between the start of successive ring draw-ins.")]
    public float ringStaggerDelay = 0.08f;

    [Tooltip("Rotation base value. " +
             "Gameplay rings: degrees/beat × bpm/60 = deg/sec (scales with active BPM). " +
             "Record rings: degrees/sec × bin fill duration in seconds.")]
    public float rotSpeedBase = 20f;

    [Tooltip("Maximum rotation speed cap in degrees/sec.")]
    public float rotSpeedMax = 300f;

    [Tooltip("Radius of the mini-ring dot that travels from note marker to tug point (in ring-local units).")]
    public float noteDotRadius = 0.02f;

    [Tooltip("Duration in seconds for each note-to-tug travel animation.")]
    public float noteTravelDuration = 0.35f;

    [Tooltip("Seconds for the ring to slide from center to off the left edge at the second loop boundary.")]
    public float rollOffDuration = 1.5f;

    [Tooltip("Duration for the quick ring appear animation (dipped contour drawn in fast).")]
    public float ringAppearDuration = 0.1f;

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

    [Tooltip("Duration of the spin-off exit in seconds (replaces rollOffDuration for gameplay rings).")]
    public float spinOffDuration = 0.45f;

    [Header("Tilt Exit")]
    [Tooltip("Target X-axis rotation (degrees) for the tilt-exit phase.")]
    public float tiltXDegrees = 75f;

    [Tooltip("Duration (seconds) for the ring to scale from its current size to zero after the tilt.")]
    public float scaleDownDuration = 0.5f;
}
