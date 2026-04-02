using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// =========================================================================
//  MotifRopeRingApplicator
//
//  MonoBehaviour that owns the MeshFilter/MeshRenderer GameObjects for the
//  3D rope-ring system.  Drop on any persistent GO.
//
//  Usage:
//    applicator.AnimateApply(snapshot);          // staggered draw-in + rotation
//    applicator.Apply(snapshot);                 // instant (no animation)
//    applicator.FitToPlayArea(w, h, cx, cy);
//    StartCoroutine(applicator.FadeOutAndClear(config.fadeOutDuration));
//
//  GyroscopeOrb sets applicator.ringOrientations before calling AnimateApply
//  so each ring gets a different 3D axis.  When null all rings are identity
//  (flat stacked, matching the 2D MotifRingGlyphApplicator layout).
// =========================================================================
public class MotifRopeRingApplicator : MonoBehaviour
{
    [Header("Config")]
    [Tooltip("ScriptableObject controlling ring layout, tube geometry, and animation.")]
    public RopeRingConfig config;

    // Populated by GyroscopeOrb (or any external owner) before calling Apply/AnimateApply.
    // Index matches ring build order (ascending BinIndex then MusicalRole).
    // null or shorter-than-ringCount arrays fall back to Quaternion.identity for missing slots.
    [HideInInspector] public Quaternion[] ringOrientations;

    // ── URP/legacy color property support ────────────────────────────────────
    private static readonly int _basePropId  = Shader.PropertyToID("_BaseColor"); // URP lit
    private static readonly int _colorPropId = Shader.PropertyToID("_Color");     // legacy

    private struct RingEntry
    {
        public MeshRenderer Renderer;
        public Mesh         Mesh;
        public Color        BaseColor;
    }

    private readonly List<RingEntry> _rings = new();
    private bool _fadingOut;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Build all ring meshes instantly with no draw-in animation.
    /// </summary>
    public void Apply(MotifSnapshot snapshot)
    {
        StopAllCoroutines();
        _fadingOut = false;
        Clear();

        if (snapshot == null || config == null) return;

        var dataList = MotifRingMeshGenerator.Generate(snapshot, config);
        for (int i = 0; i < dataList.Count; i++)
            _rings.Add(CreateRingGO(dataList[i], i));
    }

    /// <summary>
    /// Build rings with staggered draw-in animation followed by continuous rotation.
    /// Stops and replaces any previously running animation.
    /// </summary>
    public void AnimateApply(MotifSnapshot snapshot)
    {
        StopAllCoroutines();
        _fadingOut = false;
        Clear();

        if (snapshot == null || config == null) return;

        // Fill-duration lookup for per-ring rotation speed
        var fillDurs = new Dictionary<(int, float, float, float), float>();
        foreach (var bin in snapshot.TrackBins)
        {
            Color c   = bin.TrackColor;
            var   key = (bin.BinIndex, c.r, c.g, c.b);
            if (!fillDurs.TryGetValue(key, out float existing) || bin.FillDurationSeconds > existing)
                fillDurs[key] = bin.FillDurationSeconds;
        }

        var dataList = MotifRingMeshGenerator.Generate(snapshot, config);
        for (int i = 0; i < dataList.Count; i++)
        {
            var data  = dataList[i];
            var entry = CreateRingGO(data, i);

            // Wipe triangles; the coroutine reveals them progressively
            data.Mesh.SetTriangles(System.Array.Empty<int>(), 0);
            data.Mesh.RecalculateBounds();

            _rings.Add(entry);

            // Rotation speed: proportional to how long this bin took to fill
            Color lc = data.Color;
            fillDurs.TryGetValue((data.BinIndex, lc.r, lc.g, lc.b), out float fillDur);
            float rotDeg = Mathf.Clamp(
                config.rotSpeedBase * Mathf.Max(fillDur, 0.1f),
                0f, config.rotSpeedMax);
            // Alternate direction for visual depth (mirrors 2D applicator)
            if (i % 2 == 1) rotDeg = -rotDeg;

            float delay = i * config.ringStaggerDelay;
            StartCoroutine(AnimateSingleRing(
                data.Mesh, data.FullTriangles, data.TrisPerSegment,
                delay, config.ringDrawInDuration,
                entry.Renderer.transform, rotDeg));
        }
    }

    /// <summary>
    /// Fade all rings to transparent over <paramref name="duration"/> seconds then destroy them.
    /// Safe to fire-and-forget with StartCoroutine.
    /// </summary>
    public IEnumerator FadeOutAndClear(float duration)
    {
        _fadingOut = true;

        // Cache start colors and per-ring property blocks
        var startColors = new Color[_rings.Count];
        var mpbs        = new MaterialPropertyBlock[_rings.Count];
        for (int i = 0; i < _rings.Count; i++)
        {
            startColors[i] = _rings[i].BaseColor;
            mpbs[i]        = new MaterialPropertyBlock();
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float alpha = 1f - Mathf.Clamp01(elapsed / duration);

            for (int i = 0; i < _rings.Count; i++)
            {
                if (_rings[i].Renderer == null) continue;
                Color c = startColors[i];
                c.a = alpha;
                SetRendererColor(_rings[i].Renderer, c, mpbs[i]);
            }
            yield return null;
        }

        Clear();
        _fadingOut = false;
    }

    /// <summary>Destroy all ring child GameObjects and clear the ring list.</summary>
    public void Clear()
    {
        foreach (Transform child in transform)
            Destroy(child.gameObject);
        _rings.Clear();
    }

    /// <summary>
    /// Center and uniformly scale the ring group to fit within a world-space rectangle.
    /// </summary>
    public void FitToPlayArea(float width, float height, float cx, float cy)
    {
        transform.position   = new Vector3(cx, cy, transform.position.z);
        float scale          = Mathf.Min(width, height);
        transform.localScale = new Vector3(scale, scale, scale);
    }

    private void OnDestroy() => Clear();

    // ── Internal helpers ─────────────────────────────────────────────────────

    private RingEntry CreateRingGO(MotifRingMeshGenerator.RingMeshData data, int index)
    {
        var go = new GameObject(data.Name);
        go.transform.SetParent(transform, worldPositionStays: false);
        go.transform.localRotation = GetOrientation(index);

        var mf        = go.AddComponent<MeshFilter>();
        mf.sharedMesh = data.Mesh;

        var mr                = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial     = config.ringMaterial;
        mr.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows     = false;

        var mpb = new MaterialPropertyBlock();
        SetRendererColor(mr, data.Color, mpb);

        return new RingEntry { Renderer = mr, Mesh = data.Mesh, BaseColor = data.Color };
    }

    private Quaternion GetOrientation(int index)
    {
        if (ringOrientations != null && index < ringOrientations.Length)
            return ringOrientations[index];
        return Quaternion.identity;
    }

    private static void SetRendererColor(MeshRenderer mr, Color c, MaterialPropertyBlock mpb)
    {
        // Try both URP (_BaseColor) and legacy (_Color) property names
        mpb.SetColor(_basePropId, c);
        mpb.SetColor(_colorPropId, c);
        mr.SetPropertyBlock(mpb);
    }

    // ── Animation ────────────────────────────────────────────────────────────

    private IEnumerator AnimateSingleRing(
        Mesh   mesh,
        int[]  fullTris,
        int    triPerSegment,
        float  delay,
        float  drawDuration,
        Transform ringTransform,
        float  rotDegPerSec)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);

        // Phase 1: Draw-in — reveal complete spine segments progressively
        float elapsed = 0f;
        while (elapsed < drawDuration)
        {
            elapsed += Time.deltaTime;
            float progress = Mathf.Clamp01(elapsed / drawDuration);

            // Round down to nearest complete segment so only whole rings appear
            int desired = Mathf.RoundToInt(progress * fullTris.Length);
            int visible  = (desired / triPerSegment) * triPerSegment;
            visible = Mathf.Clamp(visible, 0, fullTris.Length);

            mesh.SetTriangles(fullTris, 0, visible, 0);
            mesh.RecalculateBounds();
            yield return null;
        }

        // Ensure fully drawn at the end of draw-in
        mesh.SetTriangles(fullTris, 0);
        mesh.RecalculateBounds();

        // Phase 2: Rotate continuously until FadeOutAndClear signals _fadingOut
        while (!_fadingOut)
        {
            ringTransform.Rotate(0f, 0f, rotDegPerSec * Time.deltaTime);
            yield return null;
        }
    }
}
