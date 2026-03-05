using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class SpiralCoralBaseBuilder : MonoBehaviour
{
    [Header("Prefabs")]
    [SerializeField] private GameObject baseSegmentPrefab;
    [SerializeField] private Transform baseSegmentParent;
    [Header("Spiral Shape")]
    [SerializeField, Min(0.01f)] private float startRadius = 0.25f;
    [SerializeField, Min(0.001f)] private float radiusGrowthPerRadian = 0.06f; // k in r(t)=r0+k*t
    [SerializeField, Min(0.1f)] private float totalHeight = 2.0f;
    [SerializeField, Range(0.5f, 8f)] private float turns = 3.0f;

    [Header("Sampling")]
    [SerializeField, Min(0.01f)] private float segmentSpacing = 0.08f; // world units between segments
    [SerializeField, Min(0.001f)] private float tStep = 0.02f;         // radians step for sampling loop

    [Header("Thickness")]
    [SerializeField, Min(0.01f)] private float thicknessStart = 1.15f;
    [SerializeField, Min(0.01f)] private float thicknessEnd = 0.55f;
    [SerializeField, Range(0.1f, 6f)] private float thicknessTaperPower = 1.6f;

    [Header("Material")]
    [SerializeField] private string colorProperty = "_Color"; // adjust if your shader uses a different name

    // Exposed for downstream track growth attachment
    public IReadOnlyList<Vector3> SpinePoints => _spinePoints;
    public IReadOnlyList<Quaternion> SpineFrames => _spineFrames;

    private readonly List<Vector3> _spinePoints = new();
    private readonly List<Quaternion> _spineFrames = new();
    private readonly List<Transform> _spawned = new();

    private static readonly int BaseColorId = Shader.PropertyToID("_Color");

    public void Clear()
    {
        for (int i = _spawned.Count - 1; i >= 0; i--)
        {
            if (_spawned[i] != null) Destroy(_spawned[i].gameObject);
        }
        _spawned.Clear();
        _spinePoints.Clear();
        _spineFrames.Clear();
    }

    public void BuildBase(PhaseSnapshot snapshot, int? deterministicSeed = null)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        if (baseSegmentPrefab == null)
        {
            Debug.LogError("[SpiralCoralBaseBuilder] baseSegmentPrefab is not assigned.");
            return;
        }

        Clear();

        // Deterministic seed is optional; base spiral can be fully deterministic without it.
        // If you later want gentle deterministic variation, use this seed to modulate parameters.
        int seed = deterministicSeed ?? ComputeStableSeed(snapshot);

        // If you want the base to reflect motif density subtly without new fields:
        // e.g., adjust totalHeight a little based on note count. Keep this restrained.
        int noteCount = snapshot.CollectedNotes != null ? snapshot.CollectedNotes.Count : 0;
        float density01 = Mathf.Clamp01(noteCount / 128f); // assumes "dense" around 128 total notes across tracks
        float height = Mathf.Lerp(totalHeight * 0.9f, totalHeight * 1.1f, density01);

        GenerateSpine(height);

        // Spawn segments and tint by phase color
        var mpb = new MaterialPropertyBlock();
        Color phaseColor = snapshot.Color;

        // If your shader property is not "_BaseColor", you can replace the ID lookup:
        // Use Shader.PropertyToID(colorProperty), but keep it cached if you change.
        int propId = (colorProperty == "_Color") ? BaseColorId : Shader.PropertyToID(colorProperty);

        for (int i = 0; i < _spinePoints.Count; i++)
        {
            Vector3 p = _spinePoints[i];
            Quaternion r = _spineFrames[i];
            float u = (_spinePoints.Count <= 1) ? 0f : (i / (float)(_spinePoints.Count - 1));
            float thickness = ComputeThickness(u);

            var go = Instantiate(baseSegmentPrefab, baseSegmentParent);
            go.transform.localPosition = p;
            go.transform.localRotation = r;
            go.transform.localScale = new Vector3(thickness, thickness, thickness);

            // Apply tint via MPB to avoid instantiating materials.
            var rend = go.GetComponentInChildren<Renderer>();
            if (rend != null)
            {
                mpb.Clear();
                mpb.SetColor(propId, phaseColor);
                rend.SetPropertyBlock(mpb);
            }

            _spawned.Add(go.transform);
        }
    }

    private void GenerateSpine(float height)
    {
        _spinePoints.Clear();
        _spineFrames.Clear();

        float tMax = turns * Mathf.PI * 2f;
        float hPerRadian = height / Mathf.Max(0.0001f, tMax);

        Vector3 prev = EvalSpiral(0f, hPerRadian);
        _spinePoints.Add(prev);

        float distAcc = 0f;

        for (float t = tStep; t <= tMax; t += tStep)
        {
            Vector3 p = EvalSpiral(t, hPerRadian);
            distAcc += Vector3.Distance(prev, p);

            if (distAcc >= segmentSpacing)
            {
                _spinePoints.Add(p);
                distAcc = 0f;
            }

            prev = p;
        }

        // Ensure final point is included
        Vector3 end = EvalSpiral(tMax, hPerRadian);
        if (_spinePoints.Count == 0 || Vector3.Distance(_spinePoints[_spinePoints.Count - 1], end) > 0.001f)
            _spinePoints.Add(end);

        // Frames: look along the tangent; keep Up stable
        for (int i = 0; i < _spinePoints.Count; i++)
        {
            Vector3 p = _spinePoints[i];
            Vector3 fwd;
            if (i == _spinePoints.Count - 1)
                fwd = (p - _spinePoints[i - 1]);
            else
                fwd = (_spinePoints[i + 1] - p);

            if (fwd.sqrMagnitude < 1e-8f) fwd = Vector3.forward;
            fwd.Normalize();

            // LookRotation expects Z forward. This makes segments align along the spine.
            Quaternion q = Quaternion.LookRotation(fwd, Vector3.up);
            _spineFrames.Add(q);
        }
    }

    private Vector3 EvalSpiral(float t, float hPerRadian)
    {
        float r = startRadius + radiusGrowthPerRadian * t;
        float x = r * Mathf.Cos(t);
        float z = r * Mathf.Sin(t);
        float y = hPerRadian * t;
        return new Vector3(x, y, z);
    }

    private float ComputeThickness(float u01)
    {
        // u=0 near root, u=1 at end
        float tapered = Mathf.Pow(1f - u01, thicknessTaperPower);
        return Mathf.Lerp(thicknessEnd, thicknessStart, tapered);
    }

    private int ComputeStableSeed(PhaseSnapshot snapshot)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + snapshot.Pattern.GetHashCode();
            h = h * 31 + snapshot.Color.GetHashCode();

            if (snapshot.CollectedNotes != null)
            {
                // Hash a subset deterministically to avoid heavy cost.
                int n = snapshot.CollectedNotes.Count;
                int stride = Mathf.Max(1, n / 16);
                for (int i = 0; i < n; i += stride)
                {
                    var e = snapshot.CollectedNotes[i];
                    h = h * 31 + e.Step;
                    h = h * 31 + e.Note;
                    h = h * 31 + e.Velocity.GetHashCode();
                    h = h * 31 + e.TrackColor.GetHashCode();
                }
            }
            return h;
        }
    }
}
