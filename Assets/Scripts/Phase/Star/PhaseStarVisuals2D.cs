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

    [Tooltip("Seconds for the applied visual angle to settle to a new target angle.")]
    [SerializeField, Min(0.01f)] private float diamondAngleSmoothTime = 0.06f;

    [Header("Dim (Disarmed) Look")]
    [SerializeField, Range(0f, 1f)] private float dimRgbMul = 0.55f;
    [SerializeField] private bool dimUsesFixedTint = true;
    [SerializeField] private Color rejectFlashColor = new Color(0.9f, 0.25f, 0.05f, 0.55f);
    [SerializeField, Min(0.02f)] private float rejectFlashSeconds = 0.12f;

    private Coroutine _rejectFlashRoutine;

    public ParticleSystem particles;

    [Header("Particle Debug")]
    [SerializeField] private bool traceParticleState;
    [SerializeField, Min(0f)] private float particleAlphaDriftTolerance = 0.02f;
    private float _expectedParticleAlpha = -1f;

    [Header("Dual Diamond")]
    [Tooltip("Max angular separation between the two counter-rotating diamonds at zero charge.")]
    [SerializeField] private float dualMaxSeparationDeg = 45f;

    [Header("Dim / Hidden Shard Tint")]
    [SerializeField] private Color dimShardTint = new Color(0.5f, 0.5f, 0.5f, 0.5f);

    private SpriteRenderer[] _shardSpriteRenderers;

    private SpriteRenderer _diamondASprite;
    private SpriteRenderer _diamondBSprite;

    private float _diamondAAngleCurrent;
    private float _diamondBAngleCurrent;
    private float _diamondAAngleVelocity;
    private float _diamondBAngleVelocity;
    private bool _diamondAnglesInitialized;

    public void Initialize()
    {
        CacheShardRenderers();
    }

    public void BindDualDiamondRenderers(Transform diamondA, Transform diamondB)
    {
        _diamondASprite = diamondA ? diamondA.GetComponent<SpriteRenderer>() : null;
        _diamondBSprite = diamondB ? diamondB.GetComponent<SpriteRenderer>() : null;
        ResetDualDiamondVisualState();
        CacheShardRenderers();
    }

    public void ResetDualDiamondVisualState()
    {
        _diamondAnglesInitialized = false;
        _diamondAAngleVelocity = 0f;
        _diamondBAngleVelocity = 0f;
    }

    private void CacheShardRenderers()
    {
        var srs = GetComponentsInChildren<SpriteRenderer>(true);
        var list = new List<SpriteRenderer>(srs.Length);

        for (int i = 0; i < srs.Length; i++)
        {
            var sr = srs[i];
            if (!sr) continue;
            if (sr.gameObject.name == "Scout Visual") continue;
            list.Add(sr);
        }

        _shardSpriteRenderers = list.ToArray();

        // Only fall back if explicit refs were never provided.
        if (_diamondASprite == null && _shardSpriteRenderers.Length > 0)
            _diamondASprite = _shardSpriteRenderers[0];

        if (_diamondBSprite == null && _shardSpriteRenderers.Length > 1)
            _diamondBSprite = _shardSpriteRenderers[1];
    }

    /// <summary>
    /// Clears only the generic cache. It must NOT wipe the explicit diamond refs,
    /// or BuildPreviewRing() will reintroduce renderer-order bugs.
    /// </summary>
    public void InvalidateShardCache()
    {
        _shardSpriteRenderers = null;
    }

    public void EjectParticles(GameObject ejectionPrefab)
    {
        if (ejectionPrefab != null)
            Instantiate(ejectionPrefab, transform.position, Quaternion.identity);
    }

    public void UpdateDualDiamonds(
        Color roleColor,
        float charge01,
        float rotDegA,
        bool aLocked,
        float rotDegB,
        bool bLocked,
        bool isReady,
        float readyRotMul = 2.5f)
    {
        if (_diamondASprite == null || _diamondBSprite == null)
            CacheShardRenderers();

        if (_diamondASprite == null && _diamondBSprite == null)
            return;

        Color startGray = new Color(dimShardTint.r, dimShardTint.g, dimShardTint.b, 1f);
        Color tint = Color.Lerp(startGray, roleColor, charge01 * charge01);
        tint.a = Mathf.Lerp(dimShardTint.a, 1f, charge01);

        float sep = isReady ? 0f : Mathf.Lerp(dualMaxSeparationDeg, 0f, charge01);

        float targetAngleA = aLocked ? rotDegA : (isReady ? rotDegA * readyRotMul : rotDegA) + sep;
        float targetAngleB = bLocked ? rotDegB : (isReady ? rotDegB * readyRotMul : rotDegB) - sep;

        if (!_diamondAnglesInitialized)
        {
            _diamondAAngleCurrent = targetAngleA;
            _diamondBAngleCurrent = targetAngleB;
            _diamondAnglesInitialized = true;
        }
        else
        {
            _diamondAAngleCurrent = Mathf.SmoothDampAngle(
                _diamondAAngleCurrent,
                targetAngleA,
                ref _diamondAAngleVelocity,
                diamondAngleSmoothTime);

            _diamondBAngleCurrent = Mathf.SmoothDampAngle(
                _diamondBAngleCurrent,
                targetAngleB,
                ref _diamondBAngleVelocity,
                diamondAngleSmoothTime);
        }

        Sprite shardSprite = (charge01 > 0.001f && activeDiamond != null) ? activeDiamond : diamond;

        if (_diamondASprite != null)
        {
            _diamondASprite.color = tint;
            _diamondASprite.sprite = shardSprite;
            _diamondASprite.transform.localRotation = Quaternion.Euler(0f, 0f, _diamondAAngleCurrent);
            _diamondASprite.transform.localScale = Vector3.one;
        }

        if (_diamondBSprite != null)
        {
            _diamondBSprite.color = tint;
            _diamondBSprite.sprite = shardSprite;
            _diamondBSprite.transform.localRotation = Quaternion.Euler(0f, 0f, _diamondBAngleCurrent);
            _diamondBSprite.transform.localScale = Vector3.one;
        }
    }

    public void ShowBright(Color c)
    {
        SetTintWithParticles(c);
        ToggleShardRenderers(true);
        ApplyParticleAlpha(0.85f);

        EnsureParticlesVisible(playIfStopped: true);
    }

    public void ShowDim(Color tint)
    {
        ToggleShardRenderers(true);

        Color c;
        if (dimUsesFixedTint)
        {
            c = dimShardTint;
            c.r *= dimRgbMul;
            c.g *= dimRgbMul;
            c.b *= dimRgbMul;
            c.a = dimShardTint.a;
        }
        else
        {
            c = tint;
            c.r *= dimRgbMul;
            c.g *= dimRgbMul;
            c.b *= dimRgbMul;
            c.a = dimShardTint.a;
        }

        SetShardTint(c);
        ApplyParticleAlpha(0.35f);

        EnsureParticlesVisible(playIfStopped: true);
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
        ShowDim(Color.gray);
        _rejectFlashRoutine = null;
    }

    public void HideAll()
    {
        ToggleShardRenderers(false);

        var pss = GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < pss.Length; i++)
        {
            var ps = pss[i];
            if (!ps || !ps.isPlaying) continue;
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }

    public void ShowSafetyBubble(float radiusWorld, Color bubbleTint, Color shardInnerTint, Vector2 worldCenter = default) { }
    public void HideSafetyBubble() { }
    public void UpdateBubblePosition(Vector2 worldPos) { }

    // Called every frame from PhaseStar.Update() to continuously lerp body color.
    public void LerpBodyColor(Color roleColor, float t)
    {
        Color dimColor = dimUsesFixedTint ? dimShardTint : roleColor;
        dimColor.r *= dimRgbMul;
        dimColor.g *= dimRgbMul;
        dimColor.b *= dimRgbMul;
        dimColor.a = dimShardTint.a;

        Color brightColor = roleColor;
        brightColor.a = 0.85f;

        SetShardTint(Color.Lerp(dimColor, brightColor, t));
    }

    private void SetShardTint(Color c)
    {
        if (_shardSpriteRenderers == null || _shardSpriteRenderers.Length == 0 || _shardSpriteRenderers[0] == null)
            CacheShardRenderers();

        for (int i = 0; i < _shardSpriteRenderers.Length; i++)
        {
            var sr = _shardSpriteRenderers[i];
            if (!sr) continue;
            if (sr == _diamondASprite || sr == _diamondBSprite) continue;
            sr.color = c;
        }
    }

    private void SetTintWithParticles(Color c)
    {
        c.a = 1f;

        var pss = GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < pss.Length; i++)
        {
            var ps = pss[i];
            if (!ps) continue;

            var main = ps.main;
            var start = c;
            main.startColor = new ParticleSystem.MinMaxGradient(start);

            var col = ps.colorOverLifetime;
            col.enabled = true;

            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(start, 0f), new GradientColorKey(start, 1f) },
                new[]
                {
                    new GradientAlphaKey(0, 0f),
                    new GradientAlphaKey(.5f, .5f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            col.color = new ParticleSystem.MinMaxGradient(grad);
        }

        TraceParticleState("SetTintWithParticles");
    }

    private void ApplyParticleAlpha(float a)
    {
        _expectedParticleAlpha = a;

        var pss = GetComponentsInChildren<ParticleSystem>(true);
        foreach (var ps in pss)
        {
            if (!ps) continue;
            var main = ps.main;
            var c = main.startColor.color;
            c.a = a;
            main.startColor = new ParticleSystem.MinMaxGradient(c);
        }

        TraceParticleState($"ApplyParticleAlpha({a:0.###})");
    }

    private void LateUpdate()
    {
        if (!traceParticleState || _expectedParticleAlpha < 0f) return;

        var pss = GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < pss.Length; i++)
        {
            var ps = pss[i];
            if (!ps) continue;

            float actualAlpha = ps.main.startColor.color.a;
            if (Mathf.Abs(actualAlpha - _expectedParticleAlpha) > particleAlphaDriftTolerance)
            {
                Debug.LogWarning($"[PhaseStarVisuals2D] Particle alpha drift on '{ps.name}'. Expected {_expectedParticleAlpha:0.###}, actual {actualAlpha:0.###}. Another system may be overwriting startColor.", ps);
            }
        }
    }

    private void TraceParticleState(string reason)
    {
        if (!traceParticleState) return;

        var pss = GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < pss.Length; i++)
        {
            var ps = pss[i];
            if (!ps) continue;
            var main = ps.main;
            Color c = main.startColor.color;
            Debug.Log($"[PhaseStarVisuals2D] {reason} :: '{ps.name}' activeSelf={ps.gameObject.activeSelf} playing={ps.isPlaying} rendererEnabled={ps.GetComponent<ParticleSystemRenderer>()?.enabled} startRGBA=({c.r:0.###},{c.g:0.###},{c.b:0.###},{c.a:0.###})", ps);
        }
    }

    private void EnsureParticlesVisible(bool playIfStopped)
    {
        var pss = GetComponentsInChildren<ParticleSystem>(true);
        if (pss == null || pss.Length == 0) return;

        if (!particles)
            particles = pss[0];

        for (int i = 0; i < pss.Length; i++)
        {
            var ps = pss[i];
            if (!ps) continue;

            if (!ps.gameObject.activeSelf)
                ps.gameObject.SetActive(true);

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            if (renderer) renderer.enabled = true;

            if (playIfStopped && !ps.isPlaying)
                ps.Play();
        }

        TraceParticleState($"EnsureParticlesVisible(playIfStopped={playIfStopped})");
    }
    public void ToggleShardRenderers(bool on)
    {
        if (_shardSpriteRenderers == null || _shardSpriteRenderers.Length == 0 || _shardSpriteRenderers[0] == null)
            CacheShardRenderers();

        for (int i = 0; i < _shardSpriteRenderers.Length; i++)
        {
            if (_shardSpriteRenderers[i])
                _shardSpriteRenderers[i].enabled = on;
        }
    }
}
