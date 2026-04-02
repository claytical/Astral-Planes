using UnityEngine;

// =========================================================================
//  RopeRingConfig
//
//  ScriptableObject controlling the layout, geometry, note-dip, and
//  animation parameters for the 3D rope-ring system (MotifRopeRingApplicator
//  and MotifRingMeshGenerator).  Parallels RingGlyphConfig for the 2D system.
// =========================================================================
[CreateAssetMenu(
    fileName = "RopeRingConfig",
    menuName = "Astral Planes/Visualizers/Rope Ring Config")]
public class RopeRingConfig : ScriptableObject
{
    [Header("Layout")]
    [Tooltip("Radius of the innermost ring in local units.")]
    [Min(0.01f)] public float innerRadius        = 0.08f;

    [Tooltip("Gap between successive ring surfaces.")]
    [Min(0f)]    public float ringSpacing         = 0.035f;

    [Tooltip("Tube (rope) cross-section radius as a fraction of innerRadius. " +
             "Determines how thick each ring looks.")]
    [Range(0.01f, 0.5f)] public float tubeRadiusFraction = 0.12f;

    [Header("Tube Geometry")]
    [Tooltip("Number of vertices around the tube cross-section. " +
             "4 = square rope, 8 = octagonal, 16 = smooth.")]
    [Range(4, 16)]  public int tubeSides    = 8;

    [Tooltip("Number of spine points around the ring major circumference.")]
    [Range(16, 256)] public int ringSegments = 64;

    [Header("Note Dip")]
    [Tooltip("Maximum depth of a note's inward dip as a fraction of ring radius.")]
    [Range(0f, 1f)]  public float tugDepthFraction = 0.65f;

    [Tooltip("Half-width of a note dip in radians (~0.8 ≈ 46°).")]
    [Min(0f)]        public float tugHalfWidthRad  = 0.8f;

    [Header("Animation")]
    [Tooltip("Time in seconds for a ring to fully draw itself in.")]
    [Min(0f)] public float ringDrawInDuration = 0.5f;

    [Tooltip("Delay between successive ring draw-in starts.")]
    [Min(0f)] public float ringStaggerDelay   = 0.08f;

    [Tooltip("Rotation speed (deg/s) per second of fill duration. " +
             "Actual speed = rotSpeedBase * fillDurationSeconds, clamped to rotSpeedMax.")]
    [Min(0f)] public float rotSpeedBase       = 20f;

    [Tooltip("Maximum rotation speed in deg/s.")]
    [Min(0f)] public float rotSpeedMax        = 300f;

    [Tooltip("Duration of the fade-out in seconds.")]
    [Min(0f)] public float fadeOutDuration    = 0.75f;

    [Header("Material")]
    [Tooltip("Material applied to all ring MeshRenderers. " +
             "Should support vertex color or _BaseColor/_Color property block.")]
    public Material ringMaterial;
}
