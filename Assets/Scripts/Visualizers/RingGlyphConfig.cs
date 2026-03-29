using UnityEngine;

[CreateAssetMenu(fileName = "RingGlyphConfig", menuName = "Astral Planes/Ring Glyph Config")]
public class RingGlyphConfig : ScriptableObject
{
    [Header("Layout")]
    [Tooltip("Radius of the innermost ring in local units (parent-relative).")]
    public float innerRadius = 0.08f;

    [Tooltip("Gap between the outer edge of one ring and the inner edge of the next.")]
    public float ringSpacing = 0.035f;

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

    [Tooltip("Base rotation speed in degrees/sec. Multiplied by FillDurationSeconds — " +
             "a bin filled in 5s spins at 5× this value.")]
    public float rotSpeedBase = 20f;

    [Tooltip("Maximum rotation speed cap in degrees/sec.")]
    public float rotSpeedMax = 300f;

    [Tooltip("Seconds for all rings to fade out when the bridge ends.")]
    public float fadeOutDuration = 0.75f;
}
