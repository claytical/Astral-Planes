using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds a path exit's "black hole" in code from a PathExitGeometry: a dark mass with accretion
/// rim(s) (PathExitPortal) centered on the exit's reachable boundary line, plus small bubbly
/// dust — optionally with trails — spiraling in from the playfield side and dying at the mass
/// edge. Kinds differ by mass size, rims, swirl, trails and density (see PathExitParticleStyle).
/// All dust is plain ParticleSystems, so MazePathExit's seal-fade works on them unchanged.
/// </summary>
public static class PathExitParticleBuilder
{
    private const int TexSize = 64;
    private const int TexDisc = 0;
    private const int TexRim = 1;
    private const int TexBubble = 2;
    private const int TexRing = 3;

    // Visibility floors so no exit can ever be dark-on-dark, whatever the role color or dimming.
    private const float MinSaturation = 0.35f;
    private const float MinValue = 0.75f;
    private const float MinDustAlpha = 0.6f;

    private const string Layer = "Foreground";
    private const int OrderMass = 43;
    private const int OrderRim = 44;
    private const int OrderDust = 45;
    private const int OrderRing = 46;

    private static readonly Dictionary<int, Texture2D> s_textures = new();
    private static readonly Dictionary<int, Sprite> s_sprites = new();
    private static readonly Dictionary<int, Material> s_materials = new();
    private static Material s_trailMaterial;
    private static PathExitParticleStyle s_defaultStyle;

    public static GameObject Build(
        PathExitGeometry g,
        PathExitKind kind,
        List<Color> roleColors,
        bool played,
        PathExitParticleStyle styleAsset,
        Transform parent)
    {
        var style = styleAsset != null ? styleAsset : DefaultStyle();
        var look = style.Get(kind);
        var gradient = BuildColorGradient(roleColors, played, style);
        float alpha = Mathf.Max(MinDustAlpha, look.brightness * (played ? style.playedAlpha : 1f));

        var root = new GameObject("PathExitVisual");
        root.transform.SetParent(parent, false);
        // Drawn on the TRUE screen edge so the hole reads as an opening beyond the grid (half of
        // it off-screen). Trigger and carry-out stay on g.Center, the reachable boundary.
        root.transform.position = g.VisualCenter;

        // Left/Right: the edge IS the boundary, so the kind's radius is the hole. Top/Bottom: the
        // hole must span the UI band to the grid boundary, plus a small lip scaled by kind.
        float rEff = g.ScreenEdgeDistance > 0.01f
            ? g.ScreenEdgeDistance + 0.35f * look.massRadius
            : look.massRadius;

        var mass = NewSprite("Mass", root.transform, TexDisc, OrderMass);
        var rim = NewSprite("Rim", root.transform, TexRim, OrderRim);
        var rim2 = look.rimCount > 1 ? NewSprite("Rim2", root.transform, TexRim, OrderRim) : null;

        BuildDust(root.transform, g, look, rEff, gradient, alpha);
        if (look.ringPulse) BuildRing(root.transform, rEff, gradient, alpha);

        var portal = root.AddComponent<PathExitPortal>();
        portal.Init(mass, rim, rim2,
            WithAlpha(gradient.Evaluate(0.15f), 1f), WithAlpha(gradient.Evaluate(0.85f), 1f),
            rEff, alpha, look.rimSpin);

        foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
            ps.Play();

        Debug.Log($"[PathExit] built {g.Side} kind={kind} played={played} colors={(roleColors != null ? roleColors.Count : 0)} " +
                  $"radius={rEff:F2} toScreenEdge={g.ScreenEdgeDistance:F2} alpha={alpha:F2}");
        return root;
    }

    // ── Systems ───────────────────────────────────────────────────────────────

    // Dust spirals in from a half-ring on the playfield side and dies at the mass edge.
    private static void BuildDust(Transform root, PathExitGeometry g, PathExitParticleStyle.Look look, float rEff, Gradient colors, float alpha)
    {
        float life = Mathf.Max(0.3f, look.fallSeconds);
        float emitRadius = rEff + look.massRadius * 1.4f;
        float fallDistance = emitRadius - rEff * 0.9f;

        // Circle shape's arc runs CCW from local +X, so its middle points along local +Y; rotate
        // the emitter so that middle faces Inward.
        float inwardDeg = Mathf.Atan2(g.Inward.y, g.Inward.x) * Mathf.Rad2Deg;
        var ps = NewSystem("Dust", root, Vector3.zero, Quaternion.Euler(0f, 0f, inwardDeg - 90f));

        var main = ps.main;
        main.playOnAwake = false;
        main.loop = true;
        main.prewarm = true;
        main.duration = life;
        main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.85f, life * 1.15f);
        main.startSpeed = 0f;
        main.startSize = new ParticleSystem.MinMaxCurve(look.bubbleSize * 0.6f, look.bubbleSize * 1.2f);
        main.startColor = RandomFromGradient(colors);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = Mathf.CeilToInt(look.bubbleRate * life * 1.5f) + 8;

        var emission = ps.emission;
        emission.enabled = true;
        emission.rateOverTime = look.bubbleRate;

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = emitRadius;
        shape.radiusThickness = 0f;               // emit on the rim of the ring only
        shape.arc = 180f;
        shape.arcMode = ParticleSystemShapeMultiModeValue.Random;

        // Radial pull accelerates and averages exactly 1.0, so total fall distance over the
        // lifetime equals fallDistance: bubbles reach the mass edge right as they die.
        var curve = AnimationCurve.Linear(0f, 0.5f, 1f, 1.5f);
        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.Local;
        vel.x = new ParticleSystem.MinMaxCurve(0f, curve);
        vel.y = new ParticleSystem.MinMaxCurve(0f, curve);
        vel.z = new ParticleSystem.MinMaxCurve(0f, curve);
        vel.orbitalX = new ParticleSystem.MinMaxCurve(0f, curve);
        vel.orbitalY = new ParticleSystem.MinMaxCurve(0f, curve);
        vel.orbitalZ = new ParticleSystem.MinMaxCurve(look.swirl, curve);
        vel.radial = new ParticleSystem.MinMaxCurve(-fallDistance / life, curve);

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.2f));

        SetEnvelope(ps, alpha, 0.1f, 0.85f);

        var r = ps.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.sharedMaterial = GetMaterial(TexBubble);
        r.sortingLayerName = Layer;
        r.sortingOrder = OrderDust;

        if (look.trailSeconds > 0f)
        {
            // Ribbons behind each mote read as nearby cosmic dust streaming into the hole.
            var trails = ps.trails;
            trails.enabled = true;
            trails.mode = ParticleSystemTrailMode.PerParticle;
            trails.ratio = 1f;
            trails.lifetime = new ParticleSystem.MinMaxCurve(Mathf.Clamp01(look.trailSeconds / life));
            trails.minVertexDistance = 0.04f;
            trails.worldSpace = true;
            trails.dieWithParticles = true;
            trails.sizeAffectsWidth = true;
            trails.inheritParticleColor = true;
            trails.textureMode = ParticleSystemTrailTextureMode.Stretch;
            trails.widthOverTrail = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));
            var trailFade = new Gradient();
            trailFade.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0f, 1f) });
            trails.colorOverTrail = new ParticleSystem.MinMaxGradient(trailFade);
            r.trailMaterial = GetTrailMaterial();
        }
    }

    // A ring that contracts onto the mass — new-phase only; reads as the hole drawing in.
    private static void BuildRing(Transform root, float rEff, Gradient colors, float alpha)
    {
        const float period = 1.1f;
        var ps = NewSystem("Ring", root, Vector3.zero, Quaternion.identity);

        var main = ps.main;
        main.playOnAwake = false;
        main.loop = true;
        main.prewarm = true;
        main.duration = period;
        main.startLifetime = period;
        main.startSpeed = 0f;
        main.startSize = rEff * 2f * 1.6f;
        main.startColor = RandomFromGradient(colors);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 6;

        var emission = ps.emission;
        emission.enabled = true;
        emission.rateOverTime = 1f / period;

        var shape = ps.shape;
        shape.enabled = false;

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.5f));

        SetEnvelope(ps, alpha, 0.2f, 0.8f);

        var r = ps.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.sharedMaterial = GetMaterial(TexRing);
        r.sortingLayerName = Layer;
        r.sortingOrder = OrderRing;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SpriteRenderer NewSprite(string name, Transform parent, int tex, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = GetSprite(tex);
        sr.sortingLayerName = Layer;
        sr.sortingOrder = order;
        return sr;
    }

    private static ParticleSystem NewSystem(string name, Transform parent, Vector3 localPos, Quaternion localRot)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;
        var ps = go.AddComponent<ParticleSystem>();
        // AddComponent starts playing with default settings; duration can't be changed while playing.
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        return ps;
    }

    // Fade in, hold, fade out at the mass — alpha only; hue comes from the per-particle start color.
    private static void SetEnvelope(ParticleSystem ps, float alpha, float fadeInEnd, float fadeOutStart)
    {
        var grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(alpha, fadeInEnd),
                new GradientAlphaKey(alpha, fadeOutStart),
                new GradientAlphaKey(0f, 1f),
            });
        var col = ps.colorOverLifetime;
        col.enabled = true;
        col.color = new ParticleSystem.MinMaxGradient(grad);
    }

    private static ParticleSystem.MinMaxGradient RandomFromGradient(Gradient g)
    {
        var mm = new ParticleSystem.MinMaxGradient(g);
        mm.mode = ParticleSystemGradientMode.RandomColor;
        return mm;
    }

    private static Color WithAlpha(Color c, float a) { c.a = a; return c; }

    private static Gradient BuildColorGradient(List<Color> roleColors, bool played, PathExitParticleStyle style)
    {
        var colors = new List<Color>();
        if (roleColors != null)
            foreach (var c in roleColors) colors.Add(c);
        if (colors.Count == 0) colors.Add(Color.white);

        if (played)
        {
            for (int i = 0; i < colors.Count; i++)
            {
                Color.RGBToHSV(colors[i], out var h, out var s, out var v);
                colors[i] = Color.HSVToRGB(h, s * style.playedSaturation, v * style.playedValue);
            }
        }

        // Visibility floor: neutral (near-gray) colors stay neutral, everything else is kept
        // saturated and bright enough to read against the dark playfield.
        for (int i = 0; i < colors.Count; i++)
        {
            Color.RGBToHSV(colors[i], out var h, out var s, out var v);
            if (s > 0.05f) s = Mathf.Max(s, MinSaturation);
            colors[i] = Color.HSVToRGB(h, s, Mathf.Max(v, MinValue));
        }

        var keys = new GradientColorKey[colors.Count == 1 ? 2 : colors.Count];
        if (colors.Count == 1)
        {
            keys[0] = new GradientColorKey(colors[0], 0f);
            keys[1] = new GradientColorKey(colors[0], 1f);
        }
        else
        {
            for (int i = 0; i < colors.Count; i++)
                keys[i] = new GradientColorKey(colors[i], i / (float)(colors.Count - 1));
        }

        var grad = new Gradient();
        grad.SetKeys(keys, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        return grad;
    }

    private static PathExitParticleStyle DefaultStyle()
    {
        if (s_defaultStyle == null)
        {
            s_defaultStyle = ScriptableObject.CreateInstance<PathExitParticleStyle>();
            s_defaultStyle.hideFlags = HideFlags.HideAndDontSave;
        }
        return s_defaultStyle;
    }

    // ── Procedural textures (cached; recreated if Unity destroyed them, e.g. domain reload off) ──

    private static Texture2D GetTexture(int kind)
    {
        if (s_textures.TryGetValue(kind, out var existing) && existing != null)
            return existing;

        var tex = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };
        var pixels = new Color32[TexSize * TexSize];
        for (int py = 0; py < TexSize; py++)
        {
            for (int px = 0; px < TexSize; px++)
            {
                float x = px / (float)(TexSize - 1) * 2f - 1f;
                float y = py / (float)(TexSize - 1) * 2f - 1f;
                byte a = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(Sample(kind, x, y)));
                pixels[py * TexSize + px] = new Color32(255, 255, 255, a);
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply(false, true);
        s_textures[kind] = tex;
        return tex;
    }

    // One unit wide at scale 1 (pixelsPerUnit == texture size), so scale == diameter.
    private static Sprite GetSprite(int kind)
    {
        if (s_sprites.TryGetValue(kind, out var existing) && existing != null)
            return existing;
        var sprite = Sprite.Create(GetTexture(kind), new Rect(0, 0, TexSize, TexSize), new Vector2(0.5f, 0.5f), TexSize);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        s_sprites[kind] = sprite;
        return sprite;
    }

    private static Material GetMaterial(int kind)
    {
        if (s_materials.TryGetValue(kind, out var existing) && existing != null && existing.mainTexture != null)
            return existing;
        var mat = new Material(Shader.Find("Sprites/Default")) { mainTexture = GetTexture(kind), hideFlags = HideFlags.HideAndDontSave };
        s_materials[kind] = mat;
        return mat;
    }

    private static Material GetTrailMaterial()
    {
        if (s_trailMaterial == null)
            s_trailMaterial = new Material(Shader.Find("Sprites/Default")) { hideFlags = HideFlags.HideAndDontSave };
        return s_trailMaterial;
    }

    // Alpha of texture `kind` at (x, y) in [-1,1]².
    private static float Sample(int kind, float x, float y)
    {
        float r = Mathf.Sqrt(x * x + y * y);
        switch (kind)
        {
            case TexDisc: // solid mass with a thin soft edge
                return Mathf.Clamp01((1f - r) / 0.08f);

            case TexRim:  // accretion band hugging the outer edge, brighter on two opposite arcs
            {
                float band = Mathf.Clamp01(1f - Mathf.Abs(r - 0.86f) / 0.14f);
                float arcs = 0.55f + 0.45f * Mathf.Cos(2f * Mathf.Atan2(y, x));
                return band * arcs;
            }

            case TexRing: // thin even ring for the contracting pulse
                return Mathf.Clamp01(1f - Mathf.Abs(r - 0.85f) / 0.08f);

            default:      // TexBubble — rim, faint fill and a highlight, like a small bubble
            {
                float edge = Mathf.Clamp01(1f - Mathf.Abs(r - 0.82f) / 0.14f);
                float fill = r < 0.82f ? 0.18f : 0f;
                float hx = x + 0.35f, hy = y - 0.35f;
                float highlight = Mathf.Clamp01(1f - Mathf.Sqrt(hx * hx + hy * hy) / 0.22f);
                return Mathf.Clamp01(edge + fill + highlight * 0.8f);
            }
        }
    }
}
