using System.Collections;
using UnityEngine;

// ── Ring-complete flare ──────────────────────────────────────────────────
// When a gameplay ring's last note lands (end of DeformBinRingCoroutine), the ring bursts particles
// in its colour and pulses, and a brightness wave in that colour ripples outward through the grey
// remaining-progress placeholders, then fades back. Tuned under RingGlyphConfig "Ring Complete Flare".
public partial class MotifRingGlyphApplicator
{
    private Coroutine _ringFlareRippleCoroutine;
    // Ripple envelope for the next-up placeholder (_remainingRings[0]). Its fill is owned by the
    // colour cycle, so the ripple hands it a blend weight that ApplyNextUpColor mixes in.
    private float _nextUpFlareK;
    private Color _nextUpFlareColor;

    private void PlayRingCompleteFlare(RingEntry ring, float outerR)
    {
        if (config == null || !config.enableRingCompleteFlare || ring.Root == null) return;

        SpawnRingCompleteBurst(ring, outerR);
        StartCoroutine(RingCompletePulseRoutine(ring));

        if (_ringFlareRippleCoroutine != null) StopCoroutine(_ringFlareRippleCoroutine);
        _ringFlareRippleCoroutine = StartCoroutine(RemainingRingRippleRoutine(FullBrightness(ring.BaseColor)));
    }

    private void SpawnRingCompleteBurst(RingEntry ring, float outerR)
    {
        var prefab = ringCompleteBurstPrefab != null ? ringCompleteBurstPrefab : notePlaybackFireworkPrefab;
        if (prefab == null) return;

        int points = Mathf.Max(1, config.ringCompleteBurstPoints);
        float jitter = config.ringStartAngleOffset + MotifRingGlyphGenerator.RingOrdinalAngleJitter(ring.RingOrdinal);
        for (int i = 0; i < points; i++)
        {
            float angle = i / (float)points * Mathf.PI * 2f + jitter;
            var local = new Vector3(Mathf.Cos(angle) * outerR, Mathf.Sin(angle) * outerR, 0f);
            // Parented (Local sim) so the burst rides the spinning ring, like FlashDent's firework.
            var go = Instantiate(prefab, ring.Root.transform);
            go.transform.localPosition = local;
            go.transform.localRotation = Quaternion.identity;

            var ps = go.GetComponentInChildren<ParticleSystem>();
            if (ps == null) continue;
            var main = ps.main;
            main.startColor      = ring.BaseColor;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            var psRenderer = ps.GetComponent<ParticleSystemRenderer>();
            if (psRenderer != null) psRenderer.sortingOrder = TravelDotSortingOrder;
        }
    }

    // A few quick swells of the completed ring (quick rise, slow settle — the next-up strain's
    // curve), its contour brightening toward full value on each swell.
    private IEnumerator RingCompletePulseRoutine(RingEntry ring)
    {
        var   tr         = ring.Root.transform;
        var   restScale  = tr.localScale;
        Color restStart  = ring.Contour != null ? ring.Contour.startColor : Color.clear;
        Color restEnd    = ring.Contour != null ? ring.Contour.endColor   : Color.clear;
        Color bright     = FullBrightness(ring.BaseColor);

        int   count = Mathf.Max(1, config.ringCompletePulseCount);
        float dur   = Mathf.Max(0.05f, config.ringCompletePulseDuration);
        float peak  = Mathf.Max(1f, config.ringCompletePulseScale) - 1f;

        for (float t = 0f; t < dur; t += Time.deltaTime)
        {
            if (ring.Root == null) yield break;
            float p     = Mathf.Repeat(t / dur * count, 1f);
            float swell = p < 0.2f ? Mathf.SmoothStep(0f, 1f, p / 0.2f)
                                   : 1f - Mathf.SmoothStep(0f, 1f, (p - 0.2f) / 0.8f);
            float s = 1f + peak * swell;
            tr.localScale = new Vector3(restScale.x * s, restScale.y * s, restScale.z);
            if (ring.Contour != null)
            {
                ring.Contour.startColor = LerpRgbKeepAlpha(restStart, bright, swell);
                ring.Contour.endColor   = LerpRgbKeepAlpha(restEnd,   bright, swell);
            }
            yield return null;
        }

        if (ring.Root == null) yield break;
        tr.localScale = restScale;
        if (ring.Contour != null)
        {
            ring.Contour.startColor = restStart;
            ring.Contour.endColor   = restEnd;
        }
    }

    private static Color LerpRgbKeepAlpha(Color from, Color to, float k)
    {
        var c = Color.Lerp(from, to, k);
        c.a = from.a;
        return c;
    }

    // Brightness wave moving outward ring by ring. Reads the live _remainingRings list every frame,
    // so a SetRemainingProgressRings rebuild mid-ripple just carries on with the new entries.
    private IEnumerator RemainingRingRippleRoutine(Color flareColor)
    {
        _nextUpFlareColor = flareColor;
        float step = Mathf.Max(0f, config.ringCompleteRippleStepDelay);
        float rise = Mathf.Max(0.01f, config.ringCompleteRippleRise);
        float fall = Mathf.Max(0.01f, config.ringCompleteRippleFall);

        Color baseCol   = config.remainingRingColor;
        Color restFill  = new Color(baseCol.r, baseCol.g, baseCol.b, config.remainingRingAlpha);
        Color flareFill = new Color(flareColor.r, flareColor.g, flareColor.b, config.ringCompleteFlareAlpha);

        for (float t = 0f; ; t += Time.deltaTime)
        {
            int  count = _remainingRings.Count;
            bool done  = t >= (Mathf.Max(0, count - 1)) * step + rise + fall;

            for (int i = 0; i < count; i++)
            {
                float local = t - i * step;
                float k = done || local <= 0f ? 0f
                        : local < rise        ? Mathf.SmoothStep(0f, 1f, local / rise)
                        : 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((local - rise) / fall));

                if (i == 0) { _nextUpFlareK = k; continue; }
                var ring = _remainingRings[i];
                if (ring.Root == null || ring.Fill == null) continue;
                SetFillColor(ring.Fill, Color.Lerp(restFill, flareFill, k), ScratchMpb);
            }

            if (done) break;
            yield return null;
        }

        _nextUpFlareK = 0f;
        _ringFlareRippleCoroutine = null;
    }

    private void StopRingFlareRipple()
    {
        if (_ringFlareRippleCoroutine != null)
        {
            StopCoroutine(_ringFlareRippleCoroutine);
            _ringFlareRippleCoroutine = null;
        }
        _nextUpFlareK = 0f;
    }
}
