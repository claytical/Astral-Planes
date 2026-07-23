using UnityEngine;

public partial class PhaseStarDustAffect
{
    // ---------------------------------------------------------------------------
    // NoteTether-style Bezier rendering
    // ---------------------------------------------------------------------------

    private void RebuildNoteTetherCurve(Tentacle tentacle, Vector2 starPos)
    {
        var cam = Camera.main;
        Vector3 aW = new Vector3(starPos.x, starPos.y, transform.position.z);
        Vector3 dW = new Vector3(tentacle.targetWorldPos.x, tentacle.targetWorldPos.y, transform.position.z);

        if (cam == null)
        {
            for (int i = 0; i < SplinePoints; i++)
            {
                float u = i / (float)(SplinePoints - 1);
                tentacle.linePts[i] = Vector3.Lerp(aW, dW, u);
            }
            return;
        }

        float depth = cam.orthographic
            ? 0f
            : Mathf.Abs(aW.z - cam.transform.position.z);

        Vector3 aS = cam.WorldToScreenPoint(new Vector3(aW.x, aW.y, cam.orthographic ? 0f : aW.z));
        Vector3 dS = cam.WorldToScreenPoint(new Vector3(dW.x, dW.y, cam.orthographic ? 0f : dW.z));
        aS.z = depth;
        dS.z = depth;

        Vector2 delta = (Vector2)(dS - aS);
        float dist = delta.magnitude;
        if (dist < 1f) dist = 1f;

        float sagPx = sag * 120f;
        sagPx = Mathf.Clamp(sagPx, 12f, 160f);

        Vector2 dir = delta / dist;
        Vector2 n   = new Vector2(-dir.y, dir.x);

        const float margin = 24f;
        float midY = (aS.y + dS.y) * 0.5f;
        float upY  = midY + sagPx;
        float dnY  = midY - sagPx;
        float top  = Screen.height - margin;
        float bot  = margin;

        float bendSign = 1f;
        if      (upY > top && dnY >= bot) bendSign = -1f;
        else if (dnY < bot && upY <= top) bendSign =  1f;
        else bendSign = (midY < Screen.height * 0.5f) ? 1f : -1f;

        Vector2 bend   = n * (sagPx * bendSign);
        float   c1Dist = Mathf.Clamp(dist * 0.10f, 18f, 120f);
        float   c2Dist = Mathf.Clamp(dist * 0.12f, 22f, 160f);

        Vector2 bS = (Vector2)aS + dir * c1Dist + bend;
        Vector2 cS = (Vector2)dS - dir * c2Dist + bend * 0.35f;

        float time = Time.time * noiseSpeed + tentacle.flowOffset * 10f;
        float weaveAmpPx = weaveAmplitude * 80f;
        float weaveCycles = dist / Mathf.Max(0.01f, weaveWavelength * 100f);
        float weavePhase = tentacle.lineIndexInRole * Mathf.PI * 0.8f;
        float weaveTime = Time.time * weaveSpeed;

        for (int i = 0; i < SplinePoints; i++)
        {
            float u  = i / (float)(SplinePoints - 1);
            float t  = Mathf.Pow(u, 2.2f);
            float it = 1f - t;

            Vector2 pS =
                it * it * it * (Vector2)aS +
                3f * it * it * t * bS +
                3f * it * t  * t * cS +
                t  * t  * t  * (Vector2)dS;

            float wob      = (Mathf.PerlinNoise(i * 0.17f, time) - 0.5f) * 2f;
            float wobTaper = 1f;
            const float wobOffAt = 0.80f;
            if (u > wobOffAt)
                wobTaper = 1f - Mathf.InverseLerp(wobOffAt, 1f, u);

            pS += n * wob * (noiseAmp * 35f) * wobTaper;

            float weave = Mathf.Sin((u * Mathf.PI * 2f * weaveCycles) + weaveTime + weavePhase);
            float weaveTaper = Mathf.Sin(u * Mathf.PI); // zero at root/tip
            pS += n * (weave * weaveAmpPx * weaveTaper);

            pS.x = Mathf.Clamp(pS.x, margin, Screen.width  - margin);
            pS.y = Mathf.Clamp(pS.y, margin, Screen.height - margin);

            Vector3 pW = cam.ScreenToWorldPoint(new Vector3(pS.x, pS.y, depth));
            pW.z = aW.z;
            tentacle.linePts[i] = pW;
        }
    }

    private static float ComputeCurveLength(Vector3[] pts)
    {
        float len = 0f;
        for (int i = 1; i < pts.Length; i++)
            len += Vector3.Distance(pts[i - 1], pts[i]);
        return len;
    }

    private void UpdateTentacleLine(Tentacle tentacle, Vector2 starPos, float dt)
    {
        RebuildNoteTetherCurve(tentacle, starPos);

        Color roleColor = GetRoleColor(tentacle.role);

        if (tentacle.state == TentacleState.Growing)
        {
            float fullT    = tentacle.growProgress * (SplinePoints - 1);
            int   baseIdx  = Mathf.Min(Mathf.FloorToInt(fullT), SplinePoints - 2);
            float frac     = fullT - baseIdx;
            int renderCount = Mathf.Max(2, baseIdx + 2);

            tentacle.line.positionCount = renderCount;
            for (int i = 0; i < renderCount - 1; i++)
                tentacle.line.SetPosition(i, tentacle.linePts[i]);
            tentacle.line.SetPosition(renderCount - 1,
                Vector3.Lerp(tentacle.linePts[baseIdx], tentacle.linePts[baseIdx + 1], frac));

            BuildGrowingGradient(tentacle, roleColor);
            tentacle.line.colorGradient = tentacle.gradient;
        }
        else
        {
            // Draining or Dissolving — full curve
            tentacle.line.positionCount = SplinePoints;
            tentacle.line.SetPositions(tentacle.linePts);

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

        float curveLen     = ComputeCurveLength(tentacle.linePts);
        float toEmit       = curveLen * shimmerRatePerUnit * Mathf.Max(0.0001f, dt);
        int   emitCount    = Mathf.FloorToInt(toEmit);
        Color roleColor    = GetRoleColor(tentacle.role);

        for (int i = 0; i < emitCount; i++)
        {
            int   idx = Mathf.Clamp(Mathf.RoundToInt(UnityEngine.Random.value * (SplinePoints - 1)), 0, SplinePoints - 1);
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
