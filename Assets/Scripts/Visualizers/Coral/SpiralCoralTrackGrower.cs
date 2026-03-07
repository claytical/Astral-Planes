using System;
using System.Collections;
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

    [Header("Bloom Animation")]
    [Tooltip("Seconds for a note to scale from zero to full size.")]
    [SerializeField, Min(0.01f)] private float bloomDuration = 0.18f;

    [Tooltip("Extra scale multiplier for root-note buds (keyRootMidi). Values > 1 make them larger.")]
    [SerializeField, Min(0.5f)] private float rootNoteScaleBoost = 1.4f;

    [Header("Coral Steering")]
    [Tooltip("Transform to rotate when the player steers. If null, uses transform.parent.")]
    [SerializeField] private Transform coralRoot;

    [Tooltip("Degrees per second of coral rotation driven by stick X-axis.")]
    [SerializeField] private float rotateDegsPerSec = 60f;

    [Tooltip("Idle auto-rotation speed when stick magnitude is below the dead zone.")]
    [SerializeField] private float autoRotateDegsPerSec = 8f;

    [Tooltip("Dead zone below which stick is treated as neutral for rotation (uses auto-rotate instead).")]
    [SerializeField, Range(0f, 0.5f)] private float steerDeadZone = 0.1f;

    [Tooltip("Maximum world-space sway offset applied to note positions.")]
    [SerializeField, Min(0f)] private float steerInfluenceMax = 0.4f;

    [Tooltip("Exponential decay rate of the accumulated sway offset (fraction per second). Higher = snappier return.")]
    [SerializeField, Min(0f)] private float steerDecay = 0.35f;

    // ── Runtime state ──────────────────────────────────────────────────────
    private readonly List<Transform> _spawned = new();

    // Per-note data for the animated path
    private struct BudEntry
    {
        public Transform  go;
        public Vector3    basePosLocal;  // position before sway, in baseCoralParent space
        public float      fireTimeNorm;  // 0–1 within bridge
        public float      targetScale;
        public bool       bloomed;
    }

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
        StopAllCoroutines();
        for (int i = _spawned.Count - 1; i >= 0; i--)
        {
            if (_spawned[i] != null) Destroy(_spawned[i].gameObject);
        }
        _spawned.Clear();
    }

    // ────────────────────────────────────────────────────────────────────────
    //  BuildTracks  (synchronous — unchanged, still used by garden/fallback)
    // ────────────────────────────────────────────────────────────────────────
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

            Vector3 tangent = frame * Vector3.forward;
            Vector3 up = Vector3.up;

            Vector3 n = Vector3.Cross(up, tangent);
            if (n.sqrMagnitude < 1e-6f)
                n = Vector3.Cross(Vector3.right, tangent);
            n.Normalize();
            Vector3 b = Vector3.Cross(tangent, n).normalized;

            float ang = spec.angleRad;
            Vector3 trackDir = (Mathf.Cos(ang) * n + Mathf.Sin(ang) * b).normalized;

            int note = e.Note;
            float pitch01 = NormalizeClamped(note, spec.minNote, spec.maxNote);

            float inv = 1f - pitch01;
            float curved = Mathf.Pow(inv, pitchCurvePower);

            float scaleMul = Mathf.Lerp(pitchScaleMin, pitchScaleMax, curved);
            float radial = radialOffsetMin + radialOffsetRange * curved;

            float jitter = (tangentJitter > 0f) ? (Hash01(step, note, specIndex) - 0.5f) * 2f * tangentJitter : 0f;

            Vector3 pos = pLocal
                          + trackDir * radial
                          + tangent * jitter
                          + Vector3.up * verticalLift;

            var go = Instantiate(notePrefab, baseCoralParent);
            go.transform.localPosition = pos;
            Transform accent = go.transform.Find(AccentChildName);
            if (accent != null)
            {
                float v01 = Mathf.Clamp01(e.Velocity);
                float s = Mathf.Lerp(0.2f, 1.0f, v01);
                accent.localScale = new Vector3(s, s, s) * 0.35f;
            }

            Quaternion look = Quaternion.LookRotation(trackDir, Vector3.up);

            go.transform.localScale = Vector3.one * scaleMul;

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

    // ────────────────────────────────────────────────────────────────────────
    //  GrowTracksAnimated  (bridge cinematic — notes bloom in step order)
    // ────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Animated version of BuildTracks for use during the phase bridge.
    /// Spawns all note GOs at zero scale, then blooms each one as the normalised
    /// bridge timer reaches its fireTimeNorm. Player stick input rotates the coral
    /// root and applies a persistent sway offset to all note positions.
    ///
    /// Holds for exactly <paramref name="bridgeSec"/> seconds before returning,
    /// so the caller's WaitForSeconds is not needed.
    /// </summary>
    /// <param name="snapshot">The committed motif snapshot.</param>
    /// <param name="bridgeSec">Total bridge duration in seconds.</param>
    /// <param name="getSteer">Delegate polled each frame for averaged stick input (magnitude 0–1).</param>
    public IEnumerator GrowTracksAnimated(PhaseSnapshot snapshot, float bridgeSec, Func<Vector2> getSteer)
    {
        if (snapshot == null) { yield return new WaitForSeconds(bridgeSec); yield break; }
        if (baseBuilder == null)
        {
            Debug.LogError("[SpiralCoralTrackGrower] baseBuilder is not assigned.");
            yield return new WaitForSeconds(bridgeSec);
            yield break;
        }
        if (notePrefab == null)
        {
            Debug.LogError("[SpiralCoralTrackGrower] notePrefab is not assigned.");
            yield return new WaitForSeconds(bridgeSec);
            yield break;
        }

        var spinePts    = baseBuilder.SpinePoints;
        var spineFrames = baseBuilder.SpineFrames;
        if (spinePts == null || spineFrames == null || spinePts.Count < 2 || spineFrames.Count != spinePts.Count)
        {
            Debug.LogError("[SpiralCoralTrackGrower] Spine not ready for GrowTracksAnimated. Call BuildBase() first.");
            yield return new WaitForSeconds(bridgeSec);
            yield break;
        }

        Clear();

        int totalSteps = Mathf.Max(1, snapshot.TotalSteps);

        // ── Log colour-matching stats so mismatches are visible at a glance ──
        int matchedNotes   = 0;
        int unmatchedNotes = 0;

        // ── Spawn all GOs at zero scale; build sorted bud list ──────────────
        var buds = new List<BudEntry>(snapshot.CollectedNotes?.Count ?? 0);
        var mpb  = new MaterialPropertyBlock();

        if (snapshot.CollectedNotes != null)
        {
            foreach (var e in snapshot.CollectedNotes)
            {
                if (e == null) continue;

                int specIndex = ResolveTrackSpecIndex(e.TrackColor);
                if (specIndex < 0)
                {
                    unmatchedNotes++;
                    continue;
                }
                matchedNotes++;

                TrackSpec spec = Specs[specIndex];

                int step = Mathf.Clamp(e.Step, 0, totalSteps - 1);
                int spineIndex = StepToSpineIndex(step, spinePts.Count);

                Vector3 pLocal  = spinePts[spineIndex];
                Quaternion frame = spineFrames[spineIndex];

                Vector3 tangent = frame * Vector3.forward;
                Vector3 up      = Vector3.up;
                Vector3 n2      = Vector3.Cross(up, tangent);
                if (n2.sqrMagnitude < 1e-6f) n2 = Vector3.Cross(Vector3.right, tangent);
                n2.Normalize();
                Vector3 b2 = Vector3.Cross(tangent, n2).normalized;

                float ang      = spec.angleRad;
                Vector3 trackDir = (Mathf.Cos(ang) * n2 + Mathf.Sin(ang) * b2).normalized;

                float pitch01 = NormalizeClamped(e.Note, spec.minNote, spec.maxNote);
                float inv     = 1f - pitch01;
                float curved  = Mathf.Pow(inv, pitchCurvePower);

                float scaleMul = Mathf.Lerp(pitchScaleMin, pitchScaleMax, curved);
                float radial   = radialOffsetMin + radialOffsetRange * curved;
                float jitter   = (tangentJitter > 0f) ? (Hash01(step, e.Note, specIndex) - 0.5f) * 2f * tangentJitter : 0f;

                Vector3 basePos = pLocal
                                  + trackDir * radial
                                  + tangent  * jitter
                                  + Vector3.up * verticalLift;

                // Root-note boost
                bool isRoot = (e.Note == snapshot.MotifKeyRootMidi);
                if (isRoot) scaleMul *= rootNoteScaleBoost;

                // Spawn at zero scale
                var go = Instantiate(notePrefab, baseCoralParent);
                go.transform.localPosition = basePos;
                go.transform.localScale    = Vector3.zero;

                // Accent child sizing (same as BuildTracks)
                Transform accent = go.transform.Find(AccentChildName);
                if (accent != null)
                {
                    float v01 = Mathf.Clamp01(e.Velocity);
                    float s   = Mathf.Lerp(0.2f, 1.0f, v01);
                    accent.localScale = new Vector3(s, s, s) * 0.35f;
                }

                // Material colour + velocity intensity
                var rends = go.GetComponentsInChildren<Renderer>();
                if (rends != null && rends.Length > 0)
                {
                    mpb.Clear();
                    mpb.SetColor(_colorPropId, (Color)spec.color);
                    if (_hasVelocityProp)
                    {
                        float v01      = Mathf.Clamp01(e.Velocity);
                        float intensity = Mathf.Lerp(velocityIntensityRange.x, velocityIntensityRange.y, v01);
                        mpb.SetFloat(_velocityPropId, intensity);
                    }
                    for (int r = 0; r < rends.Length; r++)
                        rends[r].SetPropertyBlock(mpb);
                }

                _spawned.Add(go.transform);

                buds.Add(new BudEntry
                {
                    go           = go.transform,
                    basePosLocal = basePos,
                    fireTimeNorm = Mathf.Clamp01(e.Step / (float)totalSteps),
                    targetScale  = scaleMul,
                    bloomed      = false,
                });
            }
        }

        // Sort by fire time so the bloom pointer is a simple forward scan
        buds.Sort((a, b2) => a.fireTimeNorm.CompareTo(b2.fireTimeNorm));

        Debug.Log($"[CoralGrow] GrowTracksAnimated: matched={matchedNotes} unmatched={unmatchedNotes} buds={buds.Count} bridgeSec={bridgeSec:F2} totalSteps={totalSteps}");
        if (unmatchedNotes > 0)
            Debug.LogWarning($"[CoralGrow] {unmatchedNotes} notes had no matching TrackSpec. Check InstrumentTrack.trackColor values vs hardcoded hex constants.");

        // Resolve which Transform to rotate
        Transform rotRoot = coralRoot != null ? coralRoot : transform.parent;

        // ── Per-frame loop ──────────────────────────────────────────────────
        float     elapsed     = 0f;
        int       nextBud     = 0;
        Vector3   steerOffset = Vector3.zero;

        while (elapsed < bridgeSec)
        {
            float dt = Time.deltaTime;
            elapsed += dt;
            float t  = elapsed / bridgeSec;

            // 1. Steer input
            Vector2 stick = getSteer != null ? getSteer() : Vector2.zero;
            float   mag   = stick.magnitude;

            if (rotRoot != null)
            {
                float rotY = (mag > steerDeadZone)
                    ? stick.x * rotateDegsPerSec * dt
                    : autoRotateDegsPerSec * dt;
                rotRoot.Rotate(0f, rotY, 0f, Space.World);
            }

            // Accumulate sway offset; exponential decay keeps it bounded
            steerOffset += new Vector3(stick.x, 0f, stick.y) * steerInfluenceMax * dt;
            steerOffset *= Mathf.Exp(-steerDecay * dt);
            steerOffset  = Vector3.ClampMagnitude(steerOffset, steerInfluenceMax);

            // 2. Bloom notes whose fire time has arrived
            while (nextBud < buds.Count && buds[nextBud].fireTimeNorm <= t)
            {
                var entry = buds[nextBud];
                if (!entry.bloomed)
                {
                    StartCoroutine(BloomNote(entry.go, entry.targetScale));
                    // Mark bloomed via index update (struct copy; update list entry)
                    buds[nextBud] = new BudEntry
                    {
                        go           = entry.go,
                        basePosLocal = entry.basePosLocal,
                        fireTimeNorm = entry.fireTimeNorm,
                        targetScale  = entry.targetScale,
                        bloomed      = true,
                    };
                }
                nextBud++;
            }

            // 3. Apply sway to ALL spawned note positions
            //    Convert world-space offset to local-parent space so it survives coral rotation
            if (steerOffset.sqrMagnitude > 1e-6f && baseCoralParent != null)
            {
                Vector3 localSway = baseCoralParent.InverseTransformDirection(steerOffset);
                for (int i = 0; i < buds.Count; i++)
                {
                    if (buds[i].go != null)
                        buds[i].go.localPosition = buds[i].basePosLocal + localSway;
                }
            }

            yield return null;
        }

        // Snap any remaining un-bloomed notes to full scale
        for (int i = nextBud; i < buds.Count; i++)
        {
            if (buds[i].go != null)
                buds[i].go.localScale = Vector3.one * buds[i].targetScale;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  BloomNote  — ease-out cubic scale-up
    // ────────────────────────────────────────────────────────────────────────
    private IEnumerator BloomNote(Transform go, float targetScale)
    {
        if (go == null) yield break;

        float elapsed = 0f;
        while (elapsed < bloomDuration)
        {
            if (go == null) yield break;
            elapsed += Time.deltaTime;
            float k     = Mathf.Clamp01(elapsed / bloomDuration);
            float eased = 1f - Mathf.Pow(1f - k, 3f);   // ease-out cubic
            go.localScale = Vector3.one * Mathf.Lerp(0f, targetScale, eased);
            yield return null;
        }

        if (go != null)
            go.localScale = Vector3.one * targetScale;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Shared helpers (unchanged from original BuildTracks logic)
    // ────────────────────────────────────────────────────────────────────────

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

    /// <summary>
    /// Resolves a NoteEntry TrackColor to a Specs index.
    /// First tries exact Color32 equality; falls back to nearest-color within a Manhattan
    /// distance threshold to tolerate minor float→Color32 rounding differences.
    /// Returns -1 if no match is found within the threshold.
    /// </summary>
    private static int ResolveTrackSpecIndex(Color c)
    {
        Color32 cc = (Color32)c;

        // Exact match first
        for (int i = 0; i < Specs.Length; i++)
            if (cc.Equals(Specs[i].color)) return i;

        // Nearest-colour fallback (Manhattan distance in R, G, B channels)
        const int kThreshold = 30;
        int best     = -1;
        int bestDist = kThreshold;
        for (int i = 0; i < Specs.Length; i++)
        {
            int d = Mathf.Abs(cc.r - Specs[i].color.r)
                  + Mathf.Abs(cc.g - Specs[i].color.g)
                  + Mathf.Abs(cc.b - Specs[i].color.b);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private static float Hash01(int a, int b, int c)
    {
        unchecked
        {
            uint h = 2166136261u;
            h = (h ^ (uint)a) * 16777619u;
            h = (h ^ (uint)b) * 16777619u;
            h = (h ^ (uint)c) * 16777619u;
            return (h & 0x00FFFFFFu) / 16777215f;
        }
    }

    private static Color32 Hex32(byte r, byte g, byte b) => new Color32(r, g, b, 255);
}
