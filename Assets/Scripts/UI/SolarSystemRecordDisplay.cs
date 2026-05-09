using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// TrackFinished scene: arranges all saved ring records as planets orbiting the player.
/// Each planet is a MotifRingGlyphApplicator that self-rotates on Y (spherical mode).
/// The orbital plane is tilted toward the camera for a classic solar-system perspective.
/// </summary>
public class SolarSystemRecordDisplay : MonoBehaviour
{
    [Header("Orbit Layout")]
    [Tooltip("Distance from center to each orbiting record (world units).")]
    [SerializeField] float orbitRadius = 3.5f;

    [Tooltip("X-axis tilt of the orbital plane in degrees. ~55 gives a classic top-down solar system look.")]
    [SerializeField] float orbitTiltDegrees = 55f;

    [Tooltip("Base orbital speed in degrees per second.")]
    [SerializeField] float orbitSpeedBase = 5f;

    [Tooltip("Adds slight speed variation per record so they drift apart over time.")]
    [SerializeField] float orbitSpeedVariance = 1.5f;

    [Header("Record Visuals")]
    [Tooltip("Uniform scale applied to each ring display. Adjust until rings are comfortably visible at the chosen orbit radius.")]
    [SerializeField] float recordScale = 2f;

    [Tooltip("Ring glyph config — use the same ScriptableObject as the rest of the game.")]
    [SerializeField] RingGlyphConfig ringConfig;

    [Tooltip("Line material for ring contours — Sprites/Default or the project's standard line material.")]
    [SerializeField] Material lineMaterial;

    private struct Planet
    {
        public Transform OrbitPivot;
        public float     OrbitSpeed;
    }

    private readonly List<Planet> _planets = new();

    void Start()
    {
        var rings = RingSessionStore.LoadAllRingsFromDisk();
        rings.Sort((a, b) => a.PhaseIndex != b.PhaseIndex
            ? a.PhaseIndex.CompareTo(b.PhaseIndex)
            : a.MotifIndex.CompareTo(b.MotifIndex));

        if (rings.Count == 0) return;

        // Parent that tilts the entire orbit toward the camera.
        var planeGo = new GameObject("OrbitalPlane");
        planeGo.transform.SetParent(transform, worldPositionStays: false);
        planeGo.transform.localRotation = Quaternion.Euler(orbitTiltDegrees, 0f, 0f);
        var planeTransform = planeGo.transform;

        for (int i = 0; i < rings.Count; i++)
        {
            float startAngle = (float)i / rings.Count * 360f;

            // Pivot rotates around the orbital plane's local Y to orbit the sun.
            var pivotGo = new GameObject($"OrbitPivot_{i}");
            pivotGo.transform.SetParent(planeTransform, worldPositionStays: false);
            pivotGo.transform.localRotation = Quaternion.Euler(0f, startAngle, 0f);

            // Anchor is placed at orbit radius along local X of the pivot.
            var anchorGo = new GameObject($"RecordAnchor_{i}");
            anchorGo.transform.SetParent(pivotGo.transform, worldPositionStays: false);
            anchorGo.transform.localPosition = new Vector3(orbitRadius, 0f, 0f);
            anchorGo.transform.localScale    = Vector3.one * recordScale;

            // Applicator renders the ring. sphericalRotation distributes
            // ring-layer spin speeds evenly for a globe-like self-rotation.
            var applicatorGo = new GameObject("RingApplicator");
            applicatorGo.transform.SetParent(anchorGo.transform, worldPositionStays: false);

            var applicator                = applicatorGo.AddComponent<MotifRingGlyphApplicator>();
            applicator.config             = ringConfig;
            applicator.lineMaterial       = lineMaterial;
            applicator.sphericalRotation  = true;
            applicator.AnimateApply(rings[i]);

            // Alternate orbit direction so adjacent planets don't clump up.
            float direction = i % 2 == 0 ? 1f : -1f;
            float variance  = rings.Count > 1 ? (float)i / (rings.Count - 1) * orbitSpeedVariance : 0f;
            float speed     = direction * (orbitSpeedBase + variance);

            _planets.Add(new Planet
            {
                OrbitPivot = pivotGo.transform,
                OrbitSpeed = speed,
            });
        }
    }

    void Update()
    {
        foreach (var p in _planets)
        {
            if (p.OrbitPivot == null) continue;
            p.OrbitPivot.Rotate(0f, p.OrbitSpeed * Time.deltaTime, 0f, Space.Self);
        }
    }
}
