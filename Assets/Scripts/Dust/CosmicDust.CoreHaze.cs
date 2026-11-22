using UnityEngine;

// Core/haze split — open-vs-closed path legibility.
//
// The prefab's particle system is the HAZE: the soft overlapping bloom sized by dustFootprintMul
// (> 1 cell, so neighbours blend), now drawn at reduced alpha. A runtime clone of it, the CORE, is
// a tighter, higher-alpha cloud sized to the collider (~1 cell). A solid cell reads as an opaque
// center; an open corridor between two walls holds only faint haze.
//
// The core is a child of the haze transform with Hierarchy scaling, so every transform-driven
// animation (grow-in / scale-out, drain shudder, nose compression) moves both layers together.
// Color goes through the shared sinks (SetDustColorAllParticles / SetRainbowParticleColor), and the
// emission, clear, hide and renderer-toggle paths already iterate every child system
// (CosmicDustParticleEmissionSystem.GetAllParticleSystems), so they cover the core automatically.
//
// Not affected by the core: block-form swell (haze only), cell-relative plasma sizing (core is
// skipped entirely when particleCellFraction > 0), contact noise burst (haze only).
//
// Tuning lives on CosmicDustGeneratorConfig ("Core / Haze"), pushed by CosmicDustGenerator.Start.
public partial class CosmicDust
{
    private const string kCoreLayerName = "DustCore";

    private static bool  s_coreEnabled      = true;
    private static float s_coreFootprintMul = 0.95f;
    private static float s_coreScatter      = 0.15f;
    private static float s_coreEmissionMul  = 0.6f;
    private static float s_coreAlphaMul     = 1f;
    private static float s_hazeAlphaMul     = 0.4f;
    private static float s_coreParticleSize = 0.6f;

    public static void SetCoreHazeParams(bool coreEnabled, float coreFootprintMul, float coreScatter,
                                         float coreEmissionMul, float coreAlphaMul, float hazeAlphaMul,
                                         float coreParticleSize)
    {
        s_coreParticleSize = Mathf.Max(0.01f, coreParticleSize);
        s_coreEnabled      = coreEnabled;
        s_coreFootprintMul = Mathf.Max(0.05f, coreFootprintMul);
        s_coreScatter      = Mathf.Max(0f, coreScatter);
        s_coreEmissionMul  = Mathf.Max(0f, coreEmissionMul);
        s_coreAlphaMul     = Mathf.Max(0f, coreAlphaMul);
        s_hazeAlphaMul     = Mathf.Clamp01(hazeAlphaMul);
    }

    private ParticleSystem _coreParticleSystem;

    // Haze only dims when a core exists to carry the wall — without one, keep the legacy look.
    private float HazeAlphaMul => _coreParticleSystem != null ? s_hazeAlphaMul : 1f;

    // Must run in Awake before _particles first caches the child systems / base emission rates.
    private void CreateCoreLayer()
    {
        var haze = visual.particleSystem;
        if (!s_coreEnabled || haze == null || _coreParticleSystem != null) return;
        if (haze.gameObject == gameObject) return;          // cloning would duplicate the whole cell
        if (visual.particleCellFraction > 0f) return;       // plasma mode: one kernel per cell

        // A cell cloned from a live instance already carries its core — reuse it.
        var existing = haze.transform.Find(kCoreLayerName);
        GameObject go = existing != null
            ? existing.gameObject
            : Instantiate(haze.gameObject, haze.transform, false);
        go.name = kCoreLayerName;

        var core = go.GetComponent<ParticleSystem>();
        if (core == null) return;

        var t = go.transform;
        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;

        if (existing == null)
        {
            var main = core.main;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy; // follow the haze transform's animations

            // Square emitter that fills its cell (core local units = cells, see ApplyCoreLayerScale),
            // so adjacent solid cores tile into one continuous field — a round emitter left dark
            // wedges at every seam and corner. Box emits along local Z, which is invisible in 2D,
            // so particles don't drift across cell edges.
            main.startSize = s_coreParticleSize;
            var shape = core.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.boxThickness = Vector3.zero;   // emit throughout the volume, not just the shell
            shape.position = Vector3.zero;
            shape.rotation = Vector3.zero;
            shape.scale = new Vector3(s_coreFootprintMul, s_coreFootprintMul, 0f);
            shape.randomPositionAmount = s_coreScatter;

            var em = core.emission;
            em.rateOverTimeMultiplier *= s_coreEmissionMul;

            var cr = go.GetComponent<ParticleSystemRenderer>();
            var hr = haze.GetComponent<ParticleSystemRenderer>();
            if (cr != null)
            {
                cr.maskInteraction = SpriteMaskInteraction.None;
                cr.sortingFudge = 0f;
                // Cores draw over every cell's haze, so a wall's body is never washed by a
                // neighbour's bloom.
                if (hr != null)
                {
                    cr.sortingLayerID = hr.sortingLayerID;
                    cr.sortingOrder = hr.sortingOrder + 1;
                }
            }
            core.Clear(true);
        }

        _coreParticleSystem = core;
        ApplyCoreLayerScale();
    }

    // Makes one core-local unit = one grid cell at rest, so the emitter box, particle size and
    // scatter are all authored in cells. The haze transform animates around _spriteScaleTarget
    // (grow-in, squash, shudder) and the core inherits that ratio. The haze uses Local scaling
    // (ignores root/grid scale) while the core uses Hierarchy, so the root's lossy scale is divided out.
    private void ApplyCoreLayerScale()
    {
        if (_coreParticleSystem == null) return;
        float rootScale = Mathf.Max(0.0001f, Mathf.Abs(transform.lossyScale.x));
        float s = _cellWorldSize / (Mathf.Max(0.01f, _spriteScaleTarget) * rootScale);
        _coreParticleSystem.transform.localScale = new Vector3(s, s, s);
    }

    // Shared recolor for one layer: startColor for future emissions plus a live rewrite of up to
    // `cap` particles. liveState memoizes the rewrite-budget answer so both layers of one call
    // spend a single budget token (0 = not asked yet, 1 = granted, -1 = denied).
    private void RecolorLayer(ParticleSystem ps, Color c, int cap, bool budgeted, ref int liveState)
    {
        var main = ps.main;
        main.startColor = c;

        int count = ps.particleCount;
        if (count <= 0) return;
        if (liveState == 0) liveState = AllowLiveRewrite(budgeted) ? 1 : -1;
        if (liveState < 0) return;

        if (_colorParticleBuffer == null || _colorParticleBuffer.Length < count)
            _colorParticleBuffer = new ParticleSystem.Particle[Mathf.Max(count, 32)];

        int actual = ps.GetParticles(_colorParticleBuffer);
        int n = Mathf.Min(actual, cap);
        for (int i = 0; i < n; i++)
            _colorParticleBuffer[i].startColor = c;
        ps.SetParticles(_colorParticleBuffer, n);
    }

    // Applies one display color to both layers: haze dimmed by HazeAlphaMul, core at full
    // (× coreAlphaMul) alpha.
    private void RecolorBothLayers(Color target, int cap, bool budgeted)
    {
        int liveState = 0;

        Color haze = target;
        haze.a *= HazeAlphaMul;
        RecolorLayer(visual.particleSystem, haze, cap, budgeted, ref liveState);

        if (_coreParticleSystem == null) return;
        Color core = target;
        core.a = Mathf.Clamp01(core.a * s_coreAlphaMul);
        RecolorLayer(_coreParticleSystem, core, cap, budgeted, ref liveState);
    }
}
