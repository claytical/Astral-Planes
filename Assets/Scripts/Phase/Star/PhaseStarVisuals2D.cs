using System;
using System.Collections;
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

    [Header("Dim (Disarmed) Look")]
    [SerializeField, Range(0f, 1f)] private float dimAlpha = 0.06f;     // very faint
    [SerializeField, Range(0f, 1f)] private float dimRgbMul = 0.08f;    // nearly gray/black
    [SerializeField] private bool dimUsesFixedTint = true;
    [SerializeField] private Color rejectFlashColor = new Color(0.9f, 0.25f, 0.05f, 0.55f);
    [SerializeField, Min(0.02f)] private float rejectFlashSeconds = 0.12f;

    private Coroutine _rejectFlashRoutine;

    public ParticleSystem particles;

    [Header("Bubble (Safe Space)")]
    [SerializeField] private Transform bubbleRoot;               // child: "Bubble Root"
    [SerializeField] private SpriteRenderer bubbleSprite;        // circle sprite on Bubble Root
    [SerializeField] private ParticleSystem bubbleEdgeParticles; // shimmer particles on Bubble Root
    [SerializeField, Range(0f, 1f)] private float bubbleFillAlpha  = 0.14f;  // soft interior glow
    [SerializeField, Range(0f, 1f)] private float bubbleEdgeAlpha  = 0.65f;  // crisp ring so it reads as a boundary

    [Header("Dim / Hidden Shard Tint")]
    [SerializeField] private Color dimShardTint = new Color(0.5f, 0.5f, 0.5f, 0.5f);

    // Cache shard renderers (exclude bubble visuals)
    private SpriteRenderer[] _shardSpriteRenderers;

    public void Initialize(PhaseStarBehaviorProfile profile, PhaseStar star)
    {
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

        var list = new System.Collections.Generic.List<SpriteRenderer>(srs.Length);
        for (int i = 0; i < srs.Length; i++)
        {
            var sr = srs[i];
            if (!sr) continue;
            if (bubbleSprite && sr == bubbleSprite) continue;
            // Exclude the scout's own renderer — it is managed by PhaseStarDustAffect
            // and must never be tinted/hidden by the visuals component.
            if (sr.gameObject.name == "Scout Visual") continue;
            list.Add(sr);
        }
        _shardSpriteRenderers = list.ToArray();
    }

    /// <summary>
    /// Forces a re-cache of shard renderers on next access.
    /// Call after BuildPreviewRing destroys old shards and creates new ones.
    /// </summary>
    public void InvalidateShardCache() => _shardSpriteRenderers = null;

    public void EjectParticles(GameObject ejectionPrefab)
    {
        if (ejectionPrefab != null)
            Instantiate(ejectionPrefab, transform.position, Quaternion.identity);
    }


    /// <summary>
    /// Per-frame update for the single accumulator diamond.
    /// Drives color (gray → roleColor), alpha (shardMinAlpha → 1), rotation, and scale pulse
    /// so the accumulator mirrors the scout's pulse phase and spins in the opposite direction.
    /// </summary>
    public void UpdateAccumulator(Color roleColor, float charge01, float rotDeg)
    {
        // Re-cache if empty or if the first entry was destroyed by a ring rebuild.
        if (_shardSpriteRenderers == null || _shardSpriteRenderers.Length == 0
            || _shardSpriteRenderers[0] == null)
            CacheShardRenderers();
        if (_shardSpriteRenderers == null || _shardSpriteRenderers.Length == 0) return;

        var sr = _shardSpriteRenderers[0];
        if (sr == null) return;

        // Color: lerp from visible gray to role color as charge builds.
        // Alpha is directly proportional to charge (25% charge → 25% alpha) so the
        // PreviewShard stays near-invisible until charge is substantial.
        // At zero charge keep the inspector-assigned gray visible at dimShardTint.a.
        Color startGray = new Color(dimShardTint.r, dimShardTint.g, dimShardTint.b, 1f);
        Color target = Color.Lerp(startGray, roleColor, charge01);
        target.a = charge01 > 0.001f ? charge01 : dimShardTint.a;
        sr.color = target;
        sr.sprite = diamond;

        // Rotation: opposite direction to scout.
        sr.transform.localRotation = Quaternion.Euler(0f, 0f, -rotDeg);

        // Scale: accumulator stays at full size — only the scout pulses in scale.
        sr.transform.localScale = Vector3.one;
    }

    public void ShowBright(Color c)
    {
        SetTintWithParticles(c);
        ToggleShardRenderers(true);
        ApplyParticleAlpha(.4f);

        if (particles) particles.Play();

        // In bright mode, bubble is not implied; PhaseStar controls it explicitly.
        // (No-op here.)
    }

    public void ShowDim(Color tint)
    {
        ToggleShardRenderers(true);

        Color c;

        if (dimUsesFixedTint)
        {
            // Near-black, barely visible.
            c = dimShardTint;
            c.r *= dimRgbMul;
            c.g *= dimRgbMul;
            c.b *= dimRgbMul;
            c.a  = dimAlpha;
        }
        else
        {
            // Dim the provided tint aggressively.
            c = tint;
            c.r *= dimRgbMul;
            c.g *= dimRgbMul;
            c.b *= dimRgbMul;
            c.a  = dimAlpha;
        }

        SetShardTint(c);

        // Particles are usually the thing that keeps “dim” feeling alive.
        ApplyParticleAlpha(0.05f);
        if (particles && particles.isPlaying) particles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }


    public void FlashReject()
    {
        if (_rejectFlashRoutine != null) StopCoroutine(_rejectFlashRoutine);
        _rejectFlashRoutine = StartCoroutine(RejectFlashRoutine());
    }

    private IEnumerator RejectFlashRoutine()
    {
        SetShardTint(rejectFlashColor);
        ApplyParticleAlpha(0.3f);
        yield return new WaitForSeconds(rejectFlashSeconds);
        ShowDim(Color.gray);   // restore dim grey state
        _rejectFlashRoutine = null;
    }

    public void HideAll()
    {
        ToggleShardRenderers(false);
        HideSafetyBubble();

        if (particles && particles.isPlaying)
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }
    public void ShowSafetyBubble(float radiusWorld, Color bubbleTint, Color shardInnerTint, Vector2 worldCenter = default)
    {
        if (!bubbleRoot) return;
        bubbleRoot.gameObject.SetActive(true);

        // Position bubble at provided world center (MineNode capture pos for gravity void),
        // falling back to local origin (star position) if no center supplied.
        if (worldCenter != default)
            bubbleRoot.position = new Vector3(worldCenter.x, worldCenter.y, bubbleRoot.position.z);
        else
            bubbleRoot.localPosition = Vector3.zero;

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

    /// <summary>
    /// Call every frame while the bubble is active to keep the bubble root
    /// anchored to the given world position (guards against any parent-transform drift).
    /// </summary>
    public void UpdateBubblePosition(Vector2 worldPos)
    {
        if (!bubbleRoot || !bubbleRoot.gameObject.activeSelf) return;
        // Set world position so the bubble stays at the activation center (e.g. MineNode capture pos)
        // rather than drifting with the parent star transform.
        bubbleRoot.position = new Vector3(worldPos.x, worldPos.y, bubbleRoot.position.z);
    }
    
    public void HighlightActive(Transform active, Color c, float alpha = 0.95f)
    {
        if (!active) return;
        var sr = active.GetComponent<SpriteRenderer>();
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
        if (_shardSpriteRenderers == null || _shardSpriteRenderers.Length == 0
            || _shardSpriteRenderers[0] == null)
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
        if (_shardSpriteRenderers == null || _shardSpriteRenderers.Length == 0
            || _shardSpriteRenderers[0] == null)
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
