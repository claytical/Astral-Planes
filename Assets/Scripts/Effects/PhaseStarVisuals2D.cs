using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class PhaseStarVisuals2D : MonoBehaviour
{
    [Header("Sprite pivots")]
    public Sprite diamond;
    [SerializeField] Transform[] spritePivots;
    [SerializeField] float maxFaceTurnDegPerSec = 12f;
    [SerializeField] float wobbleDeg = 3f;
    [SerializeField] float wobbleHz  = 0.12f;
    [SerializeField] float[] spriteAngleOffsets; 
        // layering
    const int _baseSortingOrder = 2000;
    int _activeTopBoost = 50;
    int _perPetalLayerStep = 1; 
        // spin speed control
    float _spinEnvMul = 0.25f;   // environment-derived multiplier (slows things down by default)

    PhaseStarBehaviorProfile _profile;
    Vector2 _lastVel;
    private Color _lastTint = Color.white;
    bool _visible = true;
    Vector3[] _baseLocalPos;
    public float rotationSpeed = .01f;
    float _lastAngle;

    public void Initialize(PhaseStarBehaviorProfile profile, PhaseStar star)
    {
        _profile = profile;

        // Default event hookups you already had:
        star.OnArmed += s => { GameFlowManager.Instance.activeDrumTrack.isPhaseStarActive = true;  };
        star.OnDisarmed += s => { GameFlowManager.Instance.activeDrumTrack.isPhaseStarActive = false; };

        // Bind to motion if present
        var motion = GetComponent<PhaseStarMotion2D>();
        if (motion != null) motion.OnVelocityChanged += HandleVelocity;

    }
    
    
    public void EjectParticles()
    {
        Instantiate(_profile.ejectionPrefab, transform.position, Quaternion.identity);
    }
    public void SetPreviewTint(Color c)
    {
        _lastTint = c;
    }
    public void ShowBright(Color c)
    {
        SetTintWithParticles(c);
        ToggleRenderers(true);
        ApplyParticleAlpha(.4f);
    }
    public void ShowDim(Color _ignored)
    {
        ToggleRenderers(true);                      // keep visible while dim
        ApplyParticleAlpha(0.3f);
    }
    public void HighlightActive(Transform active, Color c, float alpha = 0.95f) { 
        if (!active) return; 
        var sr = active.GetComponent<SpriteRenderer>(); 
        if (!sr) return; 
        c.a = alpha; 
        sr.color = c;
        active.localScale *= .5f;
    }
    void SetTintWithParticles(Color c)
    {
        c.a = .2f;
        // Particles (new + in-flight)
        var pss = GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < pss.Length; i++)
        {
            var ps = pss[i]; if (!ps) continue;

            // New particles
            var main = ps.main;
            var keepA = main.startColor.mode == ParticleSystemGradientMode.Color ? main.startColor.color.a : c.a;
            var start = c; start.a = keepA;
            main.startColor = new ParticleSystem.MinMaxGradient(start);

            // In-flight particles
            var col = ps.colorOverLifetime;
            col.enabled = true;

            // Flatten to a constant gradient at 'start'
            var grad = new Gradient();
            grad.SetKeys(
                new [] { new GradientColorKey(start, 0f), new GradientColorKey(start, 1f) },
                new []
                {
                    new GradientAlphaKey(0, 0f), new GradientAlphaKey(.5f, .5f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            col.color = new ParticleSystem.MinMaxGradient(grad);
        }
    }

        // Map "count → angles" for the stacked-star look Clay described.
    static readonly Dictionary<int, float[]> kPetalAngles = new Dictionary<int, float[]> {
                { 1, new[]{   0f } },
                { 2, new[]{   0f,  90f } },          // 4-point star (diamonds)
                { 3, new[]{   0f,  45f,  90f } },    // layered fan: 0/45/90
                { 4, new[]{   0f,  30f,  60f,  90f } },
                { 5, new[]{   0f,  22.5f, 45f, 67.5f, 90f } },
            };

        public float[] GetPetalAngles(int count)
        {
            if (count <= 0) return Array.Empty<float>();
            if (kPetalAngles.TryGetValue(count, out var preset)) return preset;
            // fallback: evenly subdivide 0..90 degrees
                var arr = new float[count];
            float step = 90f / Mathf.Max(1, count - 1);
            for (int i = 0; i < count; i++) arr[i] = step * i;
            return arr;
        }
        public void SetVeilOnNonActive(Color veil, Transform active)
        {
            var srs = GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < srs.Length; i++)
            {
                var sr = srs[i];
                if (!sr) continue;
                if (active != null && sr.transform == active) continue; // skip active
                var c = sr.color; c.a = veil.a; sr.color = c;
                srs[i].transform.localScale = Vector3.one;
            }
        }

    public void HideAll()
    {
        ToggleRenderers(false);
    }
    void ApplyParticleAlpha(float a)
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

    void ApplySpriteAlpha(float a)
    {
        var srs = GetComponentsInChildren<SpriteRenderer>(true);
        foreach (var t in srs)
            if (t) { var col = t.color; col.a = a; t.color = col; }
    }

    public Color GetLastTint() => _lastTint;

    void HandleVelocity(Vector2 v)
    {
        _lastVel = v;
//        OrientToVelocity(v);
    }

    public void Show() { _visible = true;  ToggleRenderers(true); }
    public void Hide() { _visible = false; ToggleRenderers(false); }

    void ToggleRenderers(bool on)
    {
        var rends = GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < rends.Length; i++)
            if (rends[i]) rends[i].enabled = on;
    }

}
