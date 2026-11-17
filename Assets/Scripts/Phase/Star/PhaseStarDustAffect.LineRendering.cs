using System.Collections.Generic;
using UnityEngine;

public partial class PhaseStarDustAffect
{
    // ---------------------------------------------------------------------------
    // Vine rendering. The drawn points are pinned to the route's corners: every
    // segment past the first fixed corner is built only from fixed corners, so it
    // holds its shape no matter how the star moves. Only the star-end segment
    // (star → first corner) is rebuilt each frame, and it springs toward its new
    // shape so a bumped star whips the near end like a vine.
    // ---------------------------------------------------------------------------

    private const int kVineSubdivisions = 8;
    private readonly List<Vector2> _vineDense = new List<Vector2>(256);
    private readonly List<float> _vineDenseFromTip = new List<float>(256);

    private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t +
                       (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                       (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    private static Vector2 Hermite(Vector2 p0, Vector2 m0, Vector2 p1, Vector2 m1, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return (2f * t3 - 3f * t2 + 1f) * p0 + (t3 - 2f * t2 + t) * m0 +
               (-2f * t3 + 3f * t2) * p1 + (t3 - t2) * m1;
    }

    // Outgoing tangent at fixed corner i (i ≥ 1), using only fixed corners.
    private static Vector2 FixedTangent(List<Vector2> n, int i)
    {
        int last = n.Count - 1;
        if (i >= last) return n[last] - n[last - 1];
        if (i + 1 >= last || i == 1) return n[i + 1] - n[i];
        return 0.5f * (n[i + 1] - n[i - 1]);
    }

    private void RebuildVineCurve(Tentacle tentacle, Vector2 starPos, float dt)
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        float cell = CellWorld(_gfm?.activeDrumTrack);
        float z = transform.position.z;
        var route = tentacle.route;
        const int sub = kVineSubdivisions;

        _vineDense.Clear();
        int frontSeg; float frontT;
        if (route.Count >= 2)
        {
            var n = route.nodes;
            int count = n.Count;
            route.Locate(route.GrownLength, out frontSeg, out frontT);

            // Star-end segment: Hermite star → first corner, matching the fixed tangent there.
            Vector2 c1 = n[1];
            Vector2 m1 = FixedTangent(n, 1);
            Vector2 m0 = c1 - starPos;
            var rootPts = tentacle.rootRenderPts;
            float k = dt > 0f ? 1f - Mathf.Exp(-vineFollowSharpness * dt) : 1f;
            for (int s = 0; s <= sub; s++)
            {
                float t = s / (float)sub;
                Vector2 desired = Hermite(starPos, m0, c1, m1, t);
                Vector3 d3 = new Vector3(desired.x, desired.y, z);
                // Pinned at both ends; the middle lags and settles.
                float stiff = Mathf.Lerp(1f, 0.35f, Mathf.Sin(t * Mathf.PI));
                if (!tentacle.rootRenderInit || s == 0 || s == sub)
                    rootPts[s] = d3;
                else
                    rootPts[s] = Vector3.Lerp(rootPts[s], d3, 1f - Mathf.Pow(1f - k, stiff));
                if (s < sub) _vineDense.Add(rootPts[s]);
            }
            tentacle.rootRenderInit = true;

            // Fixed segments: Catmull-Rom style, built from fixed corners only.
            for (int i = 1; i < count - 1; i++)
            {
                Vector2 a = n[i], b = n[i + 1];
                Vector2 ma = FixedTangent(n, i), mb = FixedTangent(n, i + 1);
                for (int s = 0; s < sub; s++)
                    _vineDense.Add(Hermite(a, ma, b, mb, s / (float)sub));
            }
            _vineDense.Add(n[count - 1]);
        }
        else
        {
            frontSeg = 0; frontT = 1f;
            _vineDense.Add(starPos);
            _vineDense.Add(tentacle.targetWorldPos);
        }

        // Drawn length: up to the grow front (dense index = segment * sub + fraction).
        int denseLast = _vineDense.Count - 1;
        float frontIdx = route.Count >= 2 ? Mathf.Min(denseLast, (frontSeg + frontT) * sub) : denseLast;
        int full = Mathf.FloorToInt(frontIdx);
        float frac = frontIdx - full;
        int drawCount = full + 1 + (frac > 1e-4f && full < denseLast ? 1 : 0);
        drawCount = Mathf.Max(2, drawCount);

        if (tentacle.linePts == null || tentacle.linePts.Length < drawCount)
            tentacle.linePts = new Vector3[Mathf.Max(drawCount, SplinePoints) * 2];

        // Distance of each drawn point from the tip end, measured along the fixed geometry, so the
        // wobble/weave pattern is anchored to the vine instead of sliding when the root moves.
        _vineDenseFromTip.Clear();
        for (int i = 0; i < drawCount; i++) _vineDenseFromTip.Add(0f);
        Vector2 Pt(int i) => i <= full ? _vineDense[i] : Vector2.Lerp(_vineDense[full], _vineDense[Mathf.Min(full + 1, denseLast)], frac);
        float acc = 0f;
        for (int i = drawCount - 2; i >= 0; i--)
        {
            acc += Vector2.Distance(Pt(i), Pt(i + 1));
            _vineDenseFromTip[i] = acc;
        }
        float drawnLen = acc;

        float maxAmp    = cell * 0.25f;
        float wobAmp    = Mathf.Min(noiseAmp * cell, maxAmp);
        float weaveAmp  = Mathf.Min(weaveAmplitude * cell * 1.5f, maxAmp);
        float time      = Time.time * noiseSpeed + tentacle.flowOffset * 10f;
        float weavePhase = tentacle.lineIndexInRole * Mathf.PI * 0.8f;
        float weaveTime = Time.time * weaveSpeed;
        float waveK     = Mathf.PI * 2f / Mathf.Max(0.01f, weaveWavelength);

        for (int i = 0; i < drawCount; i++)
        {
            Vector2 p = Pt(i);
            Vector2 tan = i < drawCount - 1 ? Pt(i + 1) - p : p - Pt(i - 1);
            Vector2 nrm = tan.sqrMagnitude > 1e-8f ? new Vector2(-tan.y, tan.x).normalized : Vector2.zero;

            float fromTip  = _vineDenseFromTip[i];
            float fromRoot = drawnLen - fromTip;
            float taper = Mathf.SmoothStep(0f, 1f, fromRoot / cell) * Mathf.SmoothStep(0f, 1f, fromTip / cell);

            float wob   = (Mathf.PerlinNoise(fromTip * 0.6f, time) - 0.5f) * 2f;
            float weave = Mathf.Sin(fromTip * waveK + weaveTime + weavePhase);
            p += nrm * ((wob * wobAmp + weave * weaveAmp) * taper);

            tentacle.linePts[i] = i == 0 ? new Vector3(starPos.x, starPos.y, z) : new Vector3(p.x, p.y, z);
        }
        tentacle.lineCount = drawCount;
    }

    private static float ComputeCurveLength(Vector3[] pts, int count)
    {
        float len = 0f;
        for (int i = 1; i < count; i++)
            len += Vector3.Distance(pts[i - 1], pts[i]);
        return len;
    }

    private void UpdateTentacleLine(Tentacle tentacle, Vector2 starPos, float dt)
    {
        RebuildVineCurve(tentacle, starPos, dt);

        Color roleColor = GetRoleColor(tentacle.role);

        // linePts already end at the grow front (or the target), so every state draws them all.
        tentacle.line.positionCount = tentacle.lineCount;
        for (int i = 0; i < tentacle.lineCount; i++)
            tentacle.line.SetPosition(i, tentacle.linePts[i]);

        if (tentacle.state == TentacleState.Growing)
        {
            BuildGrowingGradient(tentacle, roleColor);
            tentacle.line.colorGradient = tentacle.gradient;
        }
        else
        {
            float dustCharge01 = 1f;
            if (tentacle.state == TentacleState.Draining || tentacle.state == TentacleState.Dissolving)
            {
                if (_gfm == null) _gfm = GameFlowManager.Instance;
                var gen = _gfm?.dustGenerator;
                if (gen != null && gen.TryGetDustAt(tentacle.targetCell, out var dustRef) && dustRef != null)
                    dustCharge01 = dustRef.Charge01;
            }

            BuildGradient(tentacle, roleColor, dustCharge01);
            tentacle.line.colorGradient = tentacle.gradient;
        }

        tentacle.line.widthMultiplier = tentacleWidth * tentacle.alphaScale;

        if (shimmerEnabled && tentacle.shimmerPS != null &&
            (tentacle.state == TentacleState.Growing || tentacle.state == TentacleState.Draining))
        {
            EmitShimmerForTentacle(tentacle, dt);
        }
    }

    // ---------------------------------------------------------------------------
    // Shimmer
    // ---------------------------------------------------------------------------

    private void EmitShimmerForTentacle(Tentacle tentacle, float dt)
    {
        if (tentacle.shimmerPS == null || tentacle.linePts == null) return;

        int   n            = tentacle.lineCount;
        if (n < 2) return;
        float curveLen     = ComputeCurveLength(tentacle.linePts, n);
        float toEmit       = curveLen * shimmerRatePerUnit * Mathf.Max(0.0001f, dt);
        int   emitCount    = Mathf.FloorToInt(toEmit);
        Color roleColor    = GetRoleColor(tentacle.role);

        for (int i = 0; i < emitCount; i++)
        {
            int   idx = Mathf.Clamp(Mathf.RoundToInt(UnityEngine.Random.value * (n - 1)), 0, n - 1);
            Vector3 pos = tentacle.linePts[idx] + (Vector3)(UnityEngine.Random.insideUnitCircle * 0.025f);

            var ep = new ParticleSystem.EmitParams
            {
                position     = pos,
                startLifetime = shimmerLifetime * UnityEngine.Random.Range(0.8f, 1.2f),
                startSize    = shimmerSize * UnityEngine.Random.Range(0.8f, 1.3f),
                startColor   = new Color(roleColor.r, roleColor.g, roleColor.b,
                                         shimmerAlpha * UnityEngine.Random.Range(0.6f, 1f))
            };
            tentacle.shimmerPS.Emit(ep, 1);
        }
    }
}
