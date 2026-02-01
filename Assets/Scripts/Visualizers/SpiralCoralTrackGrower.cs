using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class SpiralCoralTrackGrower : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private SpiralCoralBaseBuilder baseBuilder;
    [SerializeField] private GameObject notePrefab;
    private static readonly string AccentChildName = "Accent";
    [SerializeField] private Transform baseCoralParent;

    [Header("Placement")]
    [Tooltip("How far from the spine the smallest notes sit.")]
    [SerializeField, Min(0f)] private float radialOffsetMin = 0.08f;

    [Tooltip("How far from the spine the largest notes sit (in addition to min).")]
    [SerializeField, Min(0f)] private float radialOffsetRange = 0.22f;

    [Tooltip("Small vertical lift to prevent z-fighting with the base.")]
    [SerializeField] private float verticalLift = 0.01f;

    [Tooltip("How much to jitter along the tangent for visual de-overlap (deterministic).")]
    [SerializeField, Range(0f, 0.08f)] private float tangentJitter = 0.02f;

    [Header("Scale by Pitch (Inverted)")]
    [Tooltip("Scale multiplier for the largest (lowest) notes.")]
    [SerializeField, Min(0.01f)] private float pitchScaleMax = 1.35f;

    [Tooltip("Scale multiplier for the smallest (highest) notes.")]
    [SerializeField, Min(0.01f)] private float pitchScaleMin = 0.55f;

    [Tooltip("Nonlinear curve: >1 emphasizes low notes; <1 flattens differences.")]
    [SerializeField, Range(0.25f, 4f)] private float pitchCurvePower = 1.6f;

    [Header("Velocity Expression")]
    [Tooltip("If your shader has an emission/intensity property, set its name here; otherwise leave blank.")]
    [SerializeField] private string velocityProperty = "_Intensity";

    [Tooltip("Velocity mapped into [min,max] for the above property.")]
    [SerializeField] private Vector2 velocityIntensityRange = new Vector2(0.1f, 1.0f);

    [Header("Material")]
    [SerializeField] private string colorProperty = "_Color";

    private readonly List<Transform> _spawned = new();

    // Exact constant track colors (Color32 equality-safe)
    private static readonly Color32 TrackA = Hex32(0x71, 0x00, 0xFF);
    private static readonly Color32 TrackB = Hex32(0xA4, 0xD3, 0xFF);
    private static readonly Color32 TrackC = Hex32(0x31, 0xCC, 0x7C);
    private static readonly Color32 TrackD = Hex32(0xFF, 0x99, 0x1D);

    private struct TrackSpec
    {
        public Color32 color;
        public int minNote;
        public int maxNote;
        public float angleRad; // where this track grows around the spine
        public TrackSpec(Color32 c, int minN, int maxN, float angRad)
        {
            color = c; minNote = minN; maxNote = maxN; angleRad = angRad;
        }
    }

    private static readonly TrackSpec[] Specs = new TrackSpec[]
    {
        // Angles chosen to separate tracks cleanly; rotate later if you want.
        new TrackSpec(TrackA, 36, 58, 0f),
        new TrackSpec(TrackB, 36, 78, Mathf.PI * 0.5f),
        new TrackSpec(TrackC, 52, 78, Mathf.PI * 1.0f),
        new TrackSpec(TrackD, 60, 78, Mathf.PI * 1.5f),
    };

    private int _colorPropId;
    private int _velocityPropId;
    private bool _hasVelocityProp;

    private void Awake()
    {
        _colorPropId = Shader.PropertyToID(string.IsNullOrWhiteSpace(colorProperty) ? "_Color" : colorProperty);

        _hasVelocityProp = !string.IsNullOrWhiteSpace(velocityProperty);
        if (_hasVelocityProp)
            _velocityPropId = Shader.PropertyToID(velocityProperty);
    }

    public void Clear()
    {
        for (int i = _spawned.Count - 1; i >= 0; i--)
        {
            if (_spawned[i] != null) Destroy(_spawned[i].gameObject);
        }
        _spawned.Clear();
    }

    public void BuildTracks(PhaseSnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        if (baseBuilder == null)
        {
            Debug.LogError("[SpiralCoralTrackGrower] baseBuilder is not assigned.");
            return;
        }
        if (notePrefab == null)
        {
            Debug.LogError("[SpiralCoralTrackGrower] notePrefab is not assigned.");
            return;
        }

        var spinePts = baseBuilder.SpinePoints;
        var spineFrames = baseBuilder.SpineFrames;
        if (spinePts == null || spineFrames == null || spinePts.Count < 2 || spineFrames.Count != spinePts.Count)
        {
            Debug.LogError("[SpiralCoralTrackGrower] baseBuilder spine is not ready. Call BuildBase() first.");
            return;
        }

        Clear();

        if (snapshot.CollectedNotes == null || snapshot.CollectedNotes.Count == 0)
            return;

        var mpb = new MaterialPropertyBlock();

        for (int i = 0; i < snapshot.CollectedNotes.Count; i++)
        {
            var e = snapshot.CollectedNotes[i];
            if (e == null) continue;

            int specIndex = ResolveTrackSpecIndex(e.TrackColor);
            if (specIndex < 0) continue;

            TrackSpec spec = Specs[specIndex];

            // Step -> spine index (clamp)
            int step = Mathf.Clamp(e.Step, 0, 63);
            int spineIndex = StepToSpineIndex(step, spinePts.Count);

            Vector3 pLocal = spinePts[spineIndex];
            Quaternion frame = spineFrames[spineIndex];

            // Track radial direction around the spine: use frame's up as reference.
            // We'll build a local "radial" direction in the plane orthogonal to spine tangent (frame * forward).
            Vector3 tangent = frame * Vector3.forward;
            Vector3 up = Vector3.up;

            // Build a stable orthonormal basis around tangent.
            Vector3 n = Vector3.Cross(up, tangent);
            if (n.sqrMagnitude < 1e-6f)
                n = Vector3.Cross(Vector3.right, tangent);
            n.Normalize();
            Vector3 b = Vector3.Cross(tangent, n).normalized;

            // Choose a track direction using an angle around tangent.
            float ang = spec.angleRad;
            Vector3 trackDir = (Mathf.Cos(ang) * n + Mathf.Sin(ang) * b).normalized;

            // Pitch normalization within the track's min/max
            int note = e.Note;
            float pitch01 = NormalizeClamped(note, spec.minNote, spec.maxNote);

            // Inverted: low note => big scale and bigger outward push
            float inv = 1f - pitch01;
            float curved = Mathf.Pow(inv, pitchCurvePower);

            float scaleMul = Mathf.Lerp(pitchScaleMin, pitchScaleMax, curved);
            float radial = radialOffsetMin + radialOffsetRange * curved;

            // Deterministic de-overlap: jitter along tangent by (step + note) hashed.
            float jitter = (tangentJitter > 0f) ? (Hash01(step, note, specIndex) - 0.5f) * 2f * tangentJitter : 0f;

            Vector3 pos = pLocal
                          + trackDir * radial
                          + tangent * jitter
                          + Vector3.up * verticalLift;

            // Spawn
            var go = Instantiate(notePrefab, baseCoralParent);
            go.transform.localPosition = pos;
            Transform accent = go.transform.Find(AccentChildName);
            if (accent != null)
            {
                float v01 = Mathf.Clamp01(e.Velocity);
                float s = Mathf.Lerp(0.2f, 1.0f, v01); // tune
                accent.localScale = new Vector3(s, s, s) * 0.35f; // base scale multiplier
            }

            // Orient: face outward from the spine while keeping some tangent alignment.
            // (This assumes your prefab's forward points "out". If not, rotate the prefab instead.)
            Quaternion look = Quaternion.LookRotation(trackDir, Vector3.up);

            go.transform.localScale = Vector3.one * scaleMul;

            // Tint to track color and apply velocity intensity
            var rends = go.GetComponentsInChildren<Renderer>();
            if (rends != null && rends.Length > 0)
            {
                mpb.Clear();
                mpb.SetColor(_colorPropId, (Color)spec.color);

                if (_hasVelocityProp)
                {
                    float v01 = Mathf.Clamp01(e.Velocity);
                    float intensity = Mathf.Lerp(velocityIntensityRange.x, velocityIntensityRange.y, v01);
                    mpb.SetFloat(_velocityPropId, intensity);
                }

                for (int r = 0; r < rends.Length; r++)
                    rends[r].SetPropertyBlock(mpb);
            }

            _spawned.Add(go.transform);
        }
    }

    private static int StepToSpineIndex(int step0to63, int spineCount)
    {
        if (spineCount <= 1) return 0;
        float u = step0to63 / 63f;
        int idx = Mathf.RoundToInt(u * (spineCount - 1));
        return Mathf.Clamp(idx, 0, spineCount - 1);
    }

    private static float NormalizeClamped(int v, int min, int max)
    {
        if (max <= min) return 0f;
        v = Mathf.Clamp(v, min, max);
        return (v - min) / (float)(max - min);
    }

    private static int ResolveTrackSpecIndex(Color c)
    {
        // Exact compare via Color32, since your colors are exact constants.
        Color32 cc = (Color32)c;
        for (int i = 0; i < Specs.Length; i++)
        {
            if (cc.Equals(Specs[i].color))
                return i;
        }
        return -1;
    }

    private static float Hash01(int a, int b, int c)
    {
        unchecked
        {
            uint h = 2166136261u;
            h = (h ^ (uint)a) * 16777619u;
            h = (h ^ (uint)b) * 16777619u;
            h = (h ^ (uint)c) * 16777619u;
            // map to 0..1
            return (h & 0x00FFFFFFu) / 16777215f;
        }
    }

    private static Color32 Hex32(byte r, byte g, byte b) => new Color32(r, g, b, 255);
}
