using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds a path exit's rainbow road in code from a PathExitGeometry and a PathExitConfig road
/// look. The road is fat, touching colour stripes (full spectrum or the target motif's role
/// colours; black-and-white once explored) running from inside the carved lane out past the true
/// screen edge, fading in to full opacity at the maze wall, its texture flowing outward. Stretched
/// streamers race along it; a sparse halo of motes is pulled into its mouth. Everything sorts
/// UNDER the vehicles, so a captured vehicle visibly rides the road out. The particles are plain
/// ParticleSystems (MazePathExit's seal-fade works on them unchanged); the road mesh is driven by
/// PathExitPortal.
/// </summary>
public static class PathExitParticleBuilder
{
    private const int TexSize = 128;
    private const int TexBubble = 2;
    private const int TexStreak = 3;

    // Visibility floors so no exit can ever be dark-on-dark, whatever the role colour.
    private const float MinSaturation = 0.35f;
    private const float MinValue = 0.75f;
    private const float MinParticleAlpha = 0.6f;

    // Same sorting layer as vehicles (Default, sprites at -49/-20) but below them, so the vehicle
    // is drawn ON the road rather than covered by it. The lane is carved, so no dust overlaps.
    private const string Layer = "Default";
    private const int OrderRoad = -60;
    private const int OrderParticles = -55;

    /// <summary>Rows across the road mesh: outer edge (alpha 0), feather inner, feather inner,
    /// outer edge (alpha 0). PathExitPortal ripples them for the gravitational vibration.</summary>
    public const int RoadRows = 4;

    private static readonly Dictionary<int, Texture2D> s_textures = new();
    private static readonly Dictionary<int, Material> s_materials = new();

    public static GameObject Build(
        PathExitGeometry g,
        PathExitKind kind,
        List<Color> roleColors,
        PathExitConfig cfg,
        Transform parent,
        bool entry = false)
    {
        // entry = the road the vehicles ride IN on at the next motif: it flows inward (toward the
        // maze) and fires its streamers inward from the screen edge, spraying out of the mouth;
        // no halo of motes being pulled in.
        if (cfg == null)
            cfg = GameFlowManager.Instance != null
                ? GameFlowManager.Instance.PathExitConfig
                : ScriptableObject.CreateInstance<PathExitConfig>();
        var look = cfg.Look(kind);

        var stripes = BuildStripeColors(look, roleColors);
        var gradient = GradientFrom(stripes);
        float particleAlpha = Mathf.Max(MinParticleAlpha, look.particleAlpha);

        var root = new GameObject("PathExitVisual");
        root.transform.SetParent(parent, false);
        // Anchored on the TRUE screen edge (where the road vanishes). Trigger and carry-out stay on
        // g.Center, the reachable boundary.
        root.transform.position = g.VisualCenter;

        // Road runs from part-way into the capture lane (the trigger is NOT grown to match it),
        // through the wall face and the UI band (Top/Bottom), off past the screen edge.
        float innerDist = g.ScreenEdgeDistance + g.CaptureDepth * cfg.roadInnerLength;
        float length = innerDist + cfg.offscreenTailLength;
        // Fraction of the road's length at the wall face (the maze edge being exited): the road
        // fades from transparent at its inner end up to fully opaque exactly there.
        float wallFrac = Mathf.Clamp((innerDist - g.ScreenEdgeDistance) / length, 0.02f, 0.95f);
        float width = Mathf.Max(0.1f, g.OpeningWidth * look.widthScale);
        float outwardDeg = Mathf.Atan2(g.Outward.y, g.Outward.x) * Mathf.Rad2Deg;
        Vector3 mouthLocal = g.Inward * innerDist;

        var roadGo = new GameObject("RainbowRoad");
        roadGo.transform.SetParent(root.transform, false);
        roadGo.transform.localPosition = mouthLocal;
        roadGo.transform.localRotation = Quaternion.Euler(0f, 0f, outwardDeg);
        var roadMesh = BuildRoadMesh(wallFrac, cfg.edgeFeather);
        roadGo.AddComponent<MeshFilter>().sharedMesh = roadMesh;
        var roadRenderer = roadGo.AddComponent<MeshRenderer>();
        roadRenderer.sortingLayerName = Layer;
        roadRenderer.sortingOrder = OrderRoad;
        roadRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        roadRenderer.receiveShadows = false;
        var roadTex = BuildRoadTexture(stripes, look, cfg);
        var roadMat = new Material(Shader.Find("Sprites/Default")) { mainTexture = roadTex };
        roadRenderer.sharedMaterial = roadMat;

        if (entry)
        {
            // From just past the screen edge, inward the full road and a bit beyond its mouth.
            const float startPastEdge = 0.5f;
            float inwardDeg = outwardDeg + 180f;
            BuildStreamers(root.transform, g.Outward * startPastEdge, inwardDeg, width,
                innerDist + startPastEdge, look, gradient, particleAlpha);
        }
        else
        {
            BuildStreamers(root.transform, mouthLocal, outwardDeg, width, innerDist, look, gradient, particleAlpha);
            BuildHalo(root.transform, g, mouthLocal, look, width * 0.5f, gradient, particleAlpha, strays: false);
            if (look.haloStrayFraction > 0.001f)
                BuildHalo(root.transform, g, mouthLocal, look, width * 0.5f, gradient, particleAlpha, strays: true);
        }

        var portal = root.AddComponent<PathExitPortal>();
        portal.Init(cfg, look, roadGo.transform, roadMat, roadTex, roadMesh, width, length, innerDist, reverseFlow: entry);

        foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
            ps.Play();

        if (GameFlowManager.VerboseLogging)
            Debug.Log($"[PathExit] built {g.Side} kind={kind} stripes={stripes.Count} " +
                      $"width={width:F2} length={length:F2} toScreenEdge={g.ScreenEdgeDistance:F2}");
        return root;
    }

    // ── Systems ───────────────────────────────────────────────────────────────

    // Fat speed-lines fired from `originLocal` along `travelDeg`, accelerating as they go: from the
    // mouth outward off-screen on an exit, from the screen edge inward out of the mouth on an entry.
    private static void BuildStreamers(Transform root, Vector3 originLocal, float travelDeg, float width,
        float travelDist, PathExitConfig.RoadLook look, Gradient colors, float alpha)
    {
        if (look.streamerRate <= 0.01f) return;

        const float baseSpeed = 2.5f;
        // Average of the 0.4 -> 2.5 speed curve is ~1.45; travel the road plus a bit beyond its end.
        float life = Mathf.Max(0.4f, (travelDist + 1.5f) / (baseSpeed * 1.45f));

        // Edge emitter lies along local X and fires along local +Y; rotate so +Y == travel direction.
        var ps = NewSystem("Streamers", root, originLocal, Quaternion.Euler(0f, 0f, travelDeg - 90f));

        var main = ps.main;
        main.playOnAwake = false;
        main.loop = true;
        main.prewarm = true;
        main.duration = life;
        main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.85f, life * 1.15f);
        main.startSpeed = 0f;
        main.startSize = new ParticleSystem.MinMaxCurve(look.streamerSize * 0.7f, look.streamerSize * 1.3f);
        main.startColor = RandomFromGradient(colors);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = Mathf.CeilToInt(look.streamerRate * life * 1.5f) + 8;

        var emission = ps.emission;
        emission.enabled = true;
        emission.rateOverTime = look.streamerRate;

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.SingleSidedEdge;
        shape.radius = width * 0.42f;

        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.Local;
        vel.x = ZeroCurve();
        vel.z = ZeroCurve();
        vel.y = new ParticleSystem.MinMaxCurve(baseSpeed, AnimationCurve.EaseInOut(0f, 0.4f, 1f, 2.5f));

        // Streamers deliberately bypass the MinParticleAlpha floor: they're meant to be faint.
        SetEnvelope(ps, Mathf.Clamp01(look.streamerAlpha), 0.15f, 0.85f);

        var r = ps.GetComponent<ParticleSystemRenderer>();
        // Stretched billboard: width = particle size, length = size * lengthScale (+ speed *
        // velocityScale). Speed stretch is off so the configured length is exactly what shows.
        r.renderMode = ParticleSystemRenderMode.Stretch;
        r.lengthScale = Mathf.Max(1f, look.streamerLength / Mathf.Max(0.001f, look.streamerSize));
        r.velocityScale = 0f;
        r.sharedMaterial = GetMaterial(TexStreak);
        r.sortingLayerName = Layer;
        r.sortingOrder = OrderParticles;
    }

    // A zero velocity in Curve mode, so it can sit beside curved axes in the same velocity group
    // (Unity requires x/y/z and orbitalX/Y/Z to each share one MinMaxCurve mode).
    private static ParticleSystem.MinMaxCurve ZeroCurve() =>
        new ParticleSystem.MinMaxCurve(0f, AnimationCurve.Constant(0f, 1f, 0f));

    // Halo around the road's mouth: sparse, noisy motes pulled in toward it and then along the
    // road (local -Y == Outward for this emitter). Strays share the emitter shape but have a weak
    // pull, long life and heavy noise, so some wander off instead of falling in.
    private static void BuildHalo(Transform root, PathExitGeometry g, Vector3 mouthLocal,
        PathExitConfig.RoadLook look, float halfWidth, Gradient colors, float alpha, bool strays)
    {
        float rate = look.haloRate * (strays ? look.haloStrayFraction : 1f);
        if (rate <= 0.01f) return;

        float life = Mathf.Max(0.3f, look.haloFallSeconds) * (strays ? 1.8f : 1f);
        float emitRadius = halfWidth * 2.2f;
        float fallDistance = emitRadius - halfWidth * 0.9f;
        float pull = strays ? 0.2f : 1f;

        // Circle arc runs CCW from local +X so its middle points along local +Y; rotate the
        // emitter so that middle faces Inward (into the playfield).
        float inwardDeg = Mathf.Atan2(g.Inward.y, g.Inward.x) * Mathf.Rad2Deg;
        var ps = NewSystem(strays ? "Strays" : "Halo", root, mouthLocal, Quaternion.Euler(0f, 0f, inwardDeg - 90f));

        var main = ps.main;
        main.playOnAwake = false;
        main.loop = true;
        main.prewarm = true;
        main.duration = life;
        main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.7f, life * 1.3f);
        main.startSpeed = 0f;
        float sz = look.haloSize * (strays ? 0.7f : 1f);
        main.startSize = new ParticleSystem.MinMaxCurve(sz * 0.5f, sz * 1.3f);
        main.startColor = RandomFromGradient(colors);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = Mathf.CeilToInt(rate * life * 1.5f) + 8;

        var emission = ps.emission;
        emission.enabled = true;
        emission.rateOverTime = rate;

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = emitRadius;
        shape.radiusThickness = strays ? 0.35f : 0.1f;   // slightly thick so the halo isn't a clean ring
        shape.arc = 180f;
        shape.arcMode = ParticleSystemShapeMultiModeValue.Random;

        // Radial pull accelerates (averages 1.0) so motes reach the mouth as they die; a growing
        // linear drift along Outward (-local Y) then bends them down the road.
        var curve = AnimationCurve.Linear(0f, 0.5f, 1f, 1.5f);
        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.Local;
        vel.x = ZeroCurve();
        vel.z = ZeroCurve();
        vel.y = new ParticleSystem.MinMaxCurve(-halfWidth * 2.8f * pull, AnimationCurve.Linear(0f, 0f, 1f, 1f));
        vel.orbitalX = ZeroCurve();
        vel.orbitalY = ZeroCurve();
        vel.orbitalZ = new ParticleSystem.MinMaxCurve(look.haloSwirl * (strays ? 0.5f : 1f), curve);
        vel.radial = new ParticleSystem.MinMaxCurve(-fallDistance / life * pull, curve);

        AddNoise(ps, look.haloNoise * (strays ? 2.2f : 1f), strays ? 0.6f : 1.2f);

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.2f));

        SetEnvelope(ps, alpha * (strays ? 0.8f : 1f), 0.1f, 0.85f);

        var r = ps.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.sharedMaterial = GetMaterial(TexBubble);
        r.sortingLayerName = Layer;
        r.sortingOrder = OrderParticles;
    }

    private static void AddNoise(ParticleSystem ps, float strength, float frequency)
    {
        var noise = ps.noise;
        noise.enabled = true;
        noise.separateAxes = true;
        noise.strengthX = strength;
        noise.strengthY = strength;
        noise.strengthZ = 0f;
        noise.frequency = frequency;
        noise.scrollSpeed = 0.6f;
        noise.damping = true;
        noise.quality = ParticleSystemNoiseQuality.Low;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    // Fade in, hold, fade out — alpha only; hue comes from the per-particle start colour.
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

    // Stripe colours for a road look:
    //  - Spectrum: 7 hues.
    //  - MotifRoles: the target motif's role colours (fewer, fatter stripes); fewer than 3 are
    //    padded with lighter/darker variants so the road still reads as striped. Falls back to
    //    the spectrum if the motif has no colours at all.
    //  - Monochrome: a white -> light-grey ramp, alternating so neighbours differ (never black).
    private static List<Color> BuildStripeColors(PathExitConfig.RoadLook look, List<Color> roleColors)
    {
        var colors = new List<Color>();
        bool useRoles = look.palette == PathExitConfig.RoadPalette.MotifRoles && roleColors != null && roleColors.Count > 0;
        if (useRoles)
        {
            colors.AddRange(roleColors);
            int baseCount = colors.Count;
            for (int i = 0; colors.Count < 3; i++)
            {
                Color.RGBToHSV(roleColors[i % baseCount], out var h, out var s, out var v);
                bool lighter = (i % 2) == 0;
                colors.Add(Color.HSVToRGB(h, s * (lighter ? 0.55f : 1f), lighter ? 1f : v * 0.8f));
            }
        }
        else
        {
            const int n = 7;
            for (int i = 0; i < n; i++)
                colors.Add(Color.HSVToRGB(i / (float)n, 0.85f, 1f));
        }

        if (look.monochrome)
        {
            int n = colors.Count;
            for (int i = 0; i < n; i++)
            {
                // Alternate ends of the ramp so adjacent stripes always contrast.
                int rank = (i % 2 == 0) ? i / 2 : n - 1 - i / 2;
                float g = Mathf.Lerp(0.97f, 0.55f, n > 1 ? rank / (float)(n - 1) : 0f);
                colors[i] = new Color(g, g, g, 1f);
            }
            return colors;
        }

        // Visibility floor: neutral (near-gray) colours stay neutral, everything else is kept
        // saturated and bright enough to read against the dark playfield.
        for (int i = 0; i < colors.Count; i++)
        {
            Color.RGBToHSV(colors[i], out var h, out var s, out var v);
            if (s > 0.05f) s = Mathf.Max(s, MinSaturation);
            colors[i] = Color.HSVToRGB(h, s, Mathf.Max(v, MinValue));
        }
        return colors;
    }

    private static Gradient GradientFrom(List<Color> colors)
    {
        // Gradient supports at most 8 colour keys.
        int n = Mathf.Min(colors.Count, 8);
        var keys = new GradientColorKey[Mathf.Max(2, n)];
        if (n == 1)
        {
            keys[0] = new GradientColorKey(colors[0], 0f);
            keys[1] = new GradientColorKey(colors[0], 1f);
        }
        else
        {
            for (int i = 0; i < n; i++)
                keys[i] = new GradientColorKey(colors[i], i / (float)(n - 1));
        }
        var grad = new Gradient();
        grad.SetKeys(keys, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        return grad;
    }

    // ── Road mesh / texture ───────────────────────────────────────────────────

    // x in [0,1] (along Outward), y in [-0.5,0.5] (across). PathExitPortal scales it and ripples
    // the rows. Vertex alpha eases 0 -> 1 over [0, wallFrac] — inner end transparent, fully opaque
    // at the wall face — then stays 1; the outer rows feather to 0 so the sides are soft. The fade
    // lives in the mesh, not the texture, so it doesn't scroll with the flow. Owned (and
    // destroyed) by the portal.
    private static Mesh BuildRoadMesh(float wallFrac, float feather)
    {
        const int fadeSegments = 10;   // columns easing alpha in, inner end -> wall face
        const int tailSegments = 40;   // columns past the wall, so the sides can ripple smoothly
        int cols = fadeSegments + tailSegments + 1;
        feather = Mathf.Clamp(feather, 0f, 0.45f);
        float[] rowV = { 0f, feather, 1f - feather, 1f };
        float[] rowA = { feather > 0.001f ? 0f : 1f, 1f, 1f, feather > 0.001f ? 0f : 1f };

        var verts = new Vector3[cols * RoadRows];
        var uvs = new Vector2[cols * RoadRows];
        var colors = new Color[cols * RoadRows];
        for (int i = 0; i < cols; i++)
        {
            float x, colA;
            if (i <= fadeSegments)
            {
                float t = i / (float)fadeSegments;
                x = wallFrac * t;
                colA = Smooth(t);
            }
            else
            {
                x = Mathf.Lerp(wallFrac, 1f, (i - fadeSegments) / (float)tailSegments);
                colA = 1f;
            }
            for (int r = 0; r < RoadRows; r++)
            {
                int vi = r * cols + i;
                verts[vi] = new Vector3(x, rowV[r] - 0.5f, 0f);
                uvs[vi] = new Vector2(x, rowV[r]);
                colors[vi] = new Color(1f, 1f, 1f, colA * rowA[r]);
            }
        }
        var tris = new int[(cols - 1) * (RoadRows - 1) * 6];
        int k = 0;
        for (int r = 0; r < RoadRows - 1; r++)
        {
            for (int i = 0; i < cols - 1; i++)
            {
                int b0 = r * cols + i, b1 = b0 + 1, t0 = b0 + cols, t1 = t0 + 1;
                tris[k++] = b0; tris[k++] = t0; tris[k++] = t1;
                tris[k++] = b0; tris[k++] = t1; tris[k++] = b1;
            }
        }
        var mesh = new Mesh { name = "PathExitRoad", hideFlags = HideFlags.HideAndDontSave };
        mesh.MarkDynamic();
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.colors = colors;
        mesh.triangles = tris;
        mesh.RecalculateBounds();
        return mesh;
    }

    // x = along the road (tiles seamlessly), y = colour stripes across its width. Each stripe is a
    // fat take on the ethereal ring line (RingLineEthereal / RingUnlit's _EdgeSoftness): a bright,
    // near-white core easing out to the stripe's colour. Where the ring line fades to transparent
    // at its edges, these instead blend softly into the NEIGHBOURING stripe's colour, so the road
    // stays fully opaque with nothing dark between the lines. Shimmer and the flowing pattern
    // (Chevrons for phase roads, Streaks for motif roads) only ever ADD light.
    private static Texture2D BuildRoadTexture(List<Color> stripes, PathExitConfig.RoadLook look, PathExitConfig cfg)
    {
        const int w = 128, h = 256;
        float blendWidth = Mathf.Clamp(cfg.stripeBlend, 0.001f, 0.5f);
        float coreGlow = cfg.coreGlow;
        float corePower = Mathf.Max(1f, cfg.corePower);
        float highlight = look.highlight;
        bool chevrons = look.pattern == PathExitConfig.RoadPattern.Chevrons;

        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };
        int n = Mathf.Max(1, stripes.Count);
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
        {
            float v = (y + 0.5f) / h;
            float sv = v * n;
            int idx = Mathf.Clamp(Mathf.FloorToInt(sv), 0, n - 1);
            float frac = Mathf.Clamp01(sv - idx);

            // Soft colour blend toward the neighbour near each boundary (none at the road's outer edges).
            Color stripe = stripes[idx];
            if (frac < blendWidth && idx > 0)
                stripe = Color.Lerp(stripes[idx - 1], stripe, 0.5f + 0.5f * Smooth(frac / blendWidth));
            else if (frac > 1f - blendWidth && idx < n - 1)
                stripe = Color.Lerp(stripes[idx + 1], stripe, 0.5f + 0.5f * Smooth((1f - frac) / blendWidth));

            // Ethereal core: 1 at the stripe's centre line, 0 at its edges (RingUnlit's centerDist).
            float centerDist = 1f - Mathf.Abs(frac * 2f - 1f);
            float core = Mathf.Pow(Smooth(centerDist), corePower);

            for (int x = 0; x < w; x++)
            {
                float u = x / (float)w;
                float a = u * Mathf.PI * 2f;
                // Per-stripe shimmer: seamless noise along u, offset per stripe so the lines
                // waver independently, like separate glowing strands.
                float shimmer = Mathf.PerlinNoise(Mathf.Cos(a) * 2.2f + 20f + idx * 3.7f,
                                                  Mathf.Sin(a) * 2.2f + frac * 2.5f + idx * 1.3f);

                float flow;
                if (chevrons)
                {
                    // Chevrons pointing outward (2 per tile, seamless in u).
                    float chev = Mathf.Sin(2f * Mathf.PI * (2f * u - Mathf.Abs(v - 0.5f) * 0.9f));
                    flow = 0.45f * Mathf.Pow(Mathf.Max(0f, chev), 10f);
                }
                else
                {
                    // Straight lanes: one long bright dash per stripe per tile (staggered between
                    // stripes) riding a slow, stretched streak, hugging the stripe's core.
                    float dash = Mathf.Sin(2f * Mathf.PI * (u + idx * 0.37f));
                    float slow = Mathf.PerlinNoise(Mathf.Cos(a) * 0.8f + 50f + idx * 5.1f, Mathf.Sin(a) * 0.8f + idx * 2.3f);
                    flow = (0.5f * Mathf.Pow(Mathf.Max(0f, dash), 6f) + 0.25f * slow) * (0.4f + 0.6f * core);
                }

                float k = core * coreGlow * (0.7f + 0.6f * shimmer) + highlight * (flow + 0.2f * shimmer);
                Color c = Color.Lerp(stripe, Color.white, Mathf.Clamp01(k));
                px[y * w + x] = new Color32(
                    (byte)(255f * Mathf.Clamp01(c.r)),
                    (byte)(255f * Mathf.Clamp01(c.g)),
                    (byte)(255f * Mathf.Clamp01(c.b)),
                    255);
            }
        }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return tex;
    }

    private static float Smooth(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    // ── Procedural particle textures (cached; recreated if Unity destroyed them) ──

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

    private static Material GetMaterial(int kind)
    {
        if (s_materials.TryGetValue(kind, out var existing) && existing != null && existing.mainTexture != null)
            return existing;
        var mat = new Material(Shader.Find("Sprites/Default")) { mainTexture = GetTexture(kind), hideFlags = HideFlags.HideAndDontSave };
        s_materials[kind] = mat;
        return mat;
    }

    // Alpha of texture `kind` at (x, y) in [-1,1]².
    private static float Sample(int kind, float x, float y)
    {
        float r = Mathf.Sqrt(x * x + y * y);
        switch (kind)
        {
            case TexStreak: // thin needle: solid core across, tapering to points at both ends
            {
                // Separable (box) falloff rather than radial, so it's orientation-agnostic: a
                // solid core with linear edges. Stretched to a high aspect ratio by the renderer,
                // the long axis's falloff becomes tapered tips and the short axis stays a crisp
                // line — not the round-ended ellipse a radial blob produces.
                float fx = Mathf.Clamp01((1f - Mathf.Abs(x)) / 0.45f);
                float fy = Mathf.Clamp01((1f - Mathf.Abs(y)) / 0.45f);
                return fx * fy;
            }

            default:        // TexBubble — rim, faint fill and a highlight, like a small bubble
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
