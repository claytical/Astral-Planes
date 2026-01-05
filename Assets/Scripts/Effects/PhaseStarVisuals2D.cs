using System;
using System.Collections.Generic;
using UnityEngine;
using Quaternion = UnityEngine.Quaternion;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;

[DisallowMultipleComponent]
public sealed class PhaseStarVisuals2D : MonoBehaviour
{
    [Header("Sprite pivots")]
    public Sprite diamond;
    public Sprite activeDiamond;

    private static float pulseAlphaMax = 1f;
    private static float pulseAlphaMin = .5f;
    private float alphaDirection = 1f;
    private SpriteRenderer activeSprite;

    public ParticleSystem particles;

    [Header("Bubble (Safe Space)")]
    [SerializeField] private Transform bubbleRoot;               // child: "Bubble Root"
    [SerializeField] private SpriteRenderer bubbleSprite;        // circle sprite on Bubble Root
    [SerializeField] private ParticleSystem bubbleEdgeParticles; // shimmer particles on Bubble Root
    [SerializeField, Range(0f, 1f)] private float bubbleFillAlpha = 0.08f;
    [SerializeField, Range(0f, 1f)] private float bubbleEdgeAlpha = 0.35f;

    [Header("Dim / Hidden Shard Tint")]
    [SerializeField] private Color dimShardTint = new Color(0.05f, 0.05f, 0.05f, 0.85f);

    PhaseStarBehaviorProfile _profile;
    private Color _lastTint = Color.white;

    // Cache shard renderers (exclude bubble visuals)
    private SpriteRenderer[] _shardSpriteRenderers;

    public void Initialize(PhaseStarBehaviorProfile profile, PhaseStar star)
    {
        _profile = profile;

        // Existing event hookups
        star.OnArmed += s => { GameFlowManager.Instance.activeDrumTrack.isPhaseStarActive = true; };
        star.OnDisarmed += s => { GameFlowManager.Instance.activeDrumTrack.isPhaseStarActive = false; };

        // Resolve bubble refs if not wired
        if (!bubbleRoot)
        {
            var t = transform.Find("Bubble Root");
            if (t) bubbleRoot = t;
        }
        if (bubbleRoot)
        {
            if (!bubbleSprite) bubbleSprite = bubbleRoot.GetComponentInChildren<SpriteRenderer>(true);
            if (!bubbleEdgeParticles) bubbleEdgeParticles = bubbleRoot.GetComponentInChildren<ParticleSystem>(true);
        }

        CacheShardRenderers();
        HideSafetyBubble();
    }

    private void CacheShardRenderers()
    {
        var srs = GetComponentsInChildren<SpriteRenderer>(true);

        // Exclude bubble sprite, if any
        if (bubbleSprite)
        {
            var list = new System.Collections.Generic.List<SpriteRenderer>(srs.Length);
            for (int i = 0; i < srs.Length; i++)
            {
                var sr = srs[i];
                if (!sr) continue;
                if (sr == bubbleSprite) continue;
                list.Add(sr);
            }
            _shardSpriteRenderers = list.ToArray();
        }
        else
        {
            _shardSpriteRenderers = srs;
        }
    }

    public void EjectParticles()
    {
        if (_profile != null && _profile.ejectionPrefab != null)
            Instantiate(_profile.ejectionPrefab, transform.position, Quaternion.identity);
    }

    public void SetPreviewTint(Color c) => _lastTint = c;

    public void ShowBright(Color c)
    {
        SetTintWithParticles(c);
        ToggleShardRenderers(true);
        ApplyParticleAlpha(.4f);

        if (particles) particles.Play();

        // In bright mode, bubble is not implied; PhaseStar controls it explicitly.
        // (No-op here.)
    }

    public void ShowDim(Color _ignored)
    {
        // FIX: Dim should still be visible.
        ToggleShardRenderers(true);

        // Make shards feel “nearly black”
        SetShardTint(dimShardTint);

        // Keep some subtle particle presence (optional)
        ApplyParticleAlpha(0.25f);
        if (particles && !particles.isPlaying) particles.Play();
    }

    public void HideAll()
    {
        ToggleShardRenderers(false);
        // Bubble should be hidden when the star is truly hidden
        HideSafetyBubble();
    }
    public void ShowSafetyBubble(float radiusWorld, Color bubbleTint, Color shardInnerTint)
    {
        if (!bubbleRoot) return;
        bubbleRoot.gameObject.SetActive(true);

        // Scale ONLY the fill sprite (not the whole bubbleRoot)
        if (bubbleSprite)
        {
            float diameter = Mathf.Max(0.01f, radiusWorld * 2f);
            bubbleSprite.transform.localScale = new Vector3(diameter, diameter, 1f);

            var c = bubbleTint;
            c.a = bubbleFillAlpha;
            bubbleSprite.color = c;
        }

        // Keep the particle system transform at identity scale so it doesn't drift
        if (bubbleEdgeParticles)
        {
            bubbleEdgeParticles.transform.localScale = Vector3.one;

            var shape = bubbleEdgeParticles.shape;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = radiusWorld;              // THIS is the actual edge size
            shape.radiusThickness = 0f;              // if present; keeps it a ring

            var main = bubbleEdgeParticles.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;

            var ec = bubbleTint; ec.a = bubbleEdgeAlpha;
            main.startColor = new ParticleSystem.MinMaxGradient(ec);

            if (!bubbleEdgeParticles.isPlaying) bubbleEdgeParticles.Play();
        }

        SetShardTint(shardInnerTint);
        ToggleShardRenderers(true);
    }

    public void HideSafetyBubble()
    {
        if (!bubbleRoot) return;
        bubbleRoot.gameObject.SetActive(false);

        if (bubbleEdgeParticles && bubbleEdgeParticles.isPlaying)
            bubbleEdgeParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    private void Update()
    {
        if (activeSprite != null)
        {
            Color currentColor = activeSprite.color;
            currentColor.a = Mathf.Lerp(currentColor.a, pulseAlphaMax, Time.deltaTime * alphaDirection);
            activeSprite.color = currentColor;

            if (currentColor.a <= pulseAlphaMin || currentColor.a >= pulseAlphaMax)
                alphaDirection *= -1;
        }
    }

    public void HighlightActive(Transform active, Color c, float alpha = 0.95f)
    {
        if (!active) return;
        var sr = active.GetComponent<SpriteRenderer>();
        activeSprite = sr;
        if (!sr) return;

        sr.sprite = activeDiamond;
        c.a = alpha;
        sr.color = c;
        active.localScale = active.localScale; // leaving your scale logic intact
    }

    public void SetVeilOnNonActive(Color veil, Transform active)
    {
        var srs = GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < srs.Length; i++)
        {
            var sr = srs[i];
            if (!sr) continue;

            // keep bubble alone
            if (bubbleSprite && sr == bubbleSprite) continue;

            sr.sprite = diamond;

            if (active != null && sr.transform == active) continue;

            var c = sr.color;
            c.a = veil.a;
            sr.color = c;
            srs[i].transform.localScale = Vector3.one;
        }
    }

    private void SetShardTint(Color c)
    {
        if (_shardSpriteRenderers == null || _shardSpriteRenderers.Length == 0)
            CacheShardRenderers();

        for (int i = 0; i < _shardSpriteRenderers.Length; i++)
        {
            var sr = _shardSpriteRenderers[i];
            if (!sr) continue;

            // Don’t stomp the currently “active” sprite’s alpha pulsing too harshly
            // unless you want that. For now: apply tint uniformly.
            sr.color = c;
        }
    }

    private void SetTintWithParticles(Color c)
    {
        c.a = .2f;

        var pss = GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < pss.Length; i++)
        {
            var ps = pss[i];
            if (!ps) continue;

            var main = ps.main;
            var keepA = main.startColor.mode == ParticleSystemGradientMode.Color ? main.startColor.color.a : c.a;
            var start = c; start.a = keepA;
            main.startColor = new ParticleSystem.MinMaxGradient(start);

            var col = ps.colorOverLifetime;
            col.enabled = true;

            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(start, 0f), new GradientColorKey(start, 1f) },
                new[]
                {
                    new GradientAlphaKey(0, 0f), new GradientAlphaKey(.5f, .5f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            col.color = new ParticleSystem.MinMaxGradient(grad);
        }
    }

    private void ApplyParticleAlpha(float a)
    {
        var pss = GetComponentsInChildren<ParticleSystem>(true);
        foreach (var ps in pss)
        {
            if (!ps) continue;
            var main = ps.main;
            var c = main.startColor.color; c.a = a;
            main.startColor = new ParticleSystem.MinMaxGradient(c);
        }
    }

    private void ToggleShardRenderers(bool on)
    {
        // Do NOT use Renderer[] anymore; we only want to affect shards.
        if (_shardSpriteRenderers == null || _shardSpriteRenderers.Length == 0)
            CacheShardRenderers();

        for (int i = 0; i < _shardSpriteRenderers.Length; i++)
            if (_shardSpriteRenderers[i]) _shardSpriteRenderers[i].enabled = on;
    }
    public float[] GetPetalAngles(int count, float rotationOffsetDeg = 0f)
    {
        if (count <= 0) return Array.Empty<float>();

        float[] angles = new float[count];
        float step = 360f / count;

        for (int i = 0; i < count; i++)
            angles[i] = rotationOffsetDeg + (step * i);

        return angles;
    }

}
