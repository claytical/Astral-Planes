using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// TrackFinished scene: arranges all saved ring records as flat vinyl discs orbiting the player.
/// Pivots rotate on world Y so records travel around the player (left, behind, right, in front).
/// Each disc tilts on X via a per-record wobble to read like a vinyl record from the camera.
/// Scale is capped so that no record's outermost ring exceeds maxRecordRadius world units.
/// </summary>
public class SolarSystemRecordDisplay : MonoBehaviour
{
    [Header("Orbit Layout")]
    [Tooltip("Distance from center to each orbiting record (world units).")]
    [SerializeField] float orbitRadius = 3.5f;

    [Tooltip("Base orbital speed in degrees per second.")]
    [SerializeField] float orbitSpeedBase = 5f;

    [Tooltip("Adds slight speed variation per record so they drift apart over time.")]
    [SerializeField] float orbitSpeedVariance = 1.5f;

    [Header("Record Visuals")]
    [Tooltip("Desired uniform scale for each ring display. Overridden downward if the record would exceed maxRecordRadius.")]
    [SerializeField] float recordScale = 2f;

    [Tooltip("Maximum outer-ring radius (world units) for any record. Smaller records scale up to recordScale; larger ones are capped here.")]
    [SerializeField] float maxRecordRadius = 0.5f;

    [Tooltip("Ring glyph config — use the same ScriptableObject as the rest of the game.")]
    [SerializeField] RingGlyphConfig ringConfig;

    [Tooltip("Line material for ring contours — Sprites/Default or the project's standard line material.")]
    [SerializeField] Material lineMaterial;

    [Header("Record Tilt Wobble")]
    [Tooltip("Minimum X-axis tilt of each record disc (degrees). ~60 reads like a vinyl record.")]
    [SerializeField] float wobbleTiltMin = 60f;

    [Tooltip("Maximum X-axis tilt of each record disc (degrees).")]
    [SerializeField] float wobbleTiltMax = 75f;

    [Tooltip("Speed of the tilt wobble oscillation (cycles per second).")]
    [SerializeField] float wobbleSpeed = 0.4f;

    private struct Planet
    {
        public Transform OrbitPivot;
        public Transform RecordAnchor;
        public float     OrbitSpeed;
        public float     WobblePhase;
    }

    private readonly List<Planet> _planets = new();

    void Start()
    {
        var rings = RingSessionStore.LoadAllRingsFromDisk();
        rings.Sort((a, b) => a.PhaseIndex != b.PhaseIndex
            ? a.PhaseIndex.CompareTo(b.PhaseIndex)
            : a.MotifIndex.CompareTo(b.MotifIndex));

        if (rings.Count == 0) return;

        for (int i = 0; i < rings.Count; i++)
        {
            float startAngle = (float)i / rings.Count * 360f;

            // Pivot is a direct child of this transform — local Y = world Y.
            // Rotating on local Y produces a horizontal orbit in the XZ plane,
            // so records travel around the player rather than facing the camera.
            var pivotGo = new GameObject($"OrbitPivot_{i}");
            pivotGo.transform.SetParent(transform, worldPositionStays: false);
            pivotGo.transform.localRotation = Quaternion.Euler(0f, startAngle, 0f);

            // Cap scale so no record's outermost ring exceeds maxRecordRadius.
            int   n      = CountRingKeys(rings[i]);
            float outerR = ringConfig != null && n > 0
                ? ringConfig.innerRadius + Mathf.Max(0, n - 1) * (ringConfig.ringThickness + ringConfig.ringSpacing) + ringConfig.ringThickness
                : 1f;
            float scale  = ringConfig != null && n > 0
                ? Mathf.Min(recordScale, maxRecordRadius / Mathf.Max(outerR, 0.001f))
                : recordScale;

            var anchorGo = new GameObject($"RecordAnchor_{i}");
            anchorGo.transform.SetParent(pivotGo.transform, worldPositionStays: false);
            anchorGo.transform.localPosition = new Vector3(orbitRadius, 0f, 0f);
            anchorGo.transform.localScale    = Vector3.one * scale;

            var applicatorGo = new GameObject("RingApplicator");
            applicatorGo.transform.SetParent(anchorGo.transform, worldPositionStays: false);

            var applicator               = applicatorGo.AddComponent<MotifRingGlyphApplicator>();
            applicator.config            = ringConfig;
            applicator.lineMaterial      = lineMaterial;
            applicator.sphericalRotation = false;
            applicator.AnimateApply(rings[i]);

            float variance = rings.Count > 1 ? (float)i / (rings.Count - 1) * orbitSpeedVariance : 0f;
            float speed    = orbitSpeedBase + variance;

            // Stagger wobble phases so records don't all tilt in unison.
            float phase = rings.Count > 1 ? (float)i / rings.Count * Mathf.PI * 2f : 0f;

            _planets.Add(new Planet
            {
                OrbitPivot   = pivotGo.transform,
                RecordAnchor = anchorGo.transform,
                OrbitSpeed   = speed,
                WobblePhase  = phase,
            });
        }
    }

    void Update()
    {
        foreach (var p in _planets)
        {
            if (p.OrbitPivot == null) continue;
            p.OrbitPivot.Rotate(0f, p.OrbitSpeed * Time.deltaTime, 0f, Space.Self);

            float t     = (Mathf.Sin(Time.time * wobbleSpeed * Mathf.PI * 2f + p.WobblePhase) + 1f) * 0.5f;
            float tiltX = Mathf.Lerp(wobbleTiltMin, wobbleTiltMax, t);
            p.RecordAnchor.localRotation = Quaternion.Euler(tiltX, 0f, 0f);
        }
    }

    private int CountRingKeys(MotifSnapshot snapshot)
    {
        var seen = new HashSet<(int, MusicalRole)>();
        foreach (var bin in snapshot.TrackBins
            .Where(b => b.IsFilled || b.CollectedSteps.Count > 0)
            .OrderBy(b => b.BinIndex).ThenBy(b => (int)b.Role))
            seen.Add((bin.BinIndex, bin.Role));
        if (seen.Count > 0) return seen.Count;

        var seenC = new HashSet<(int, float, float, float)>();
        foreach (var n in snapshot.CollectedNotes.OrderBy(n => n.BinIndex))
        {
            Color c = n.TrackColor;
            seenC.Add((n.BinIndex, c.r, c.g, c.b));
        }
        return seenC.Count;
    }
}
