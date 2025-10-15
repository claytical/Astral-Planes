using UnityEngine;

[DisallowMultipleComponent]
public sealed class PhaseStarVisuals2D : MonoBehaviour
{
    [Header("Sprite pivots (rotated to face motion)")]
    [SerializeField] Transform[] spritePivots;
    [SerializeField] float maxFaceTurnDegPerSec = 12f;
    [SerializeField] float wobbleDeg = 3f;
    [SerializeField] float wobbleHz  = 0.12f;
    [SerializeField] float[] spriteAngleOffsets;

    PhaseStarBehaviorProfile _profile;
    Vector2 _lastVel;
    private Color _lastTint = Color.white;
    bool _visible = true;
    Vector3[] _baseLocalPos;

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
    
    public void SetTint(Color c) { 
        _lastTint = c; 
        // Sprites
        var srs = GetComponentsInChildren<SpriteRenderer>(true); 
        for (int i = 0; i < srs.Length; i++) 
            if (srs[i]) srs[i].color = c;
        // Particles (set startColor; keeps existing alpha curves)
        var pss = GetComponentsInChildren<ParticleSystem>(true); 
        for (int i = 0; i < pss.Length; i++) {
            var ps = pss[i]; 
            if (!ps) continue; 
            var main = ps.main; 
            main.startColor = new ParticleSystem.MinMaxGradient(c);
        }
    }
// PhaseStarVisuals2D.cs
    public void SetPreviewTint(Color c)
    {
        Debug.Log($"Setting Preview Tint Color: {c}");
        // Sprites
        var srs = GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < srs.Length; i++)
            if (srs[i]) srs[i].color = c;

        // Particles (new + in-flight)  ← Add this block to mirror CosmicDust
        var pss = GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < pss.Length; i++)
        {
            var ps = pss[i]; if (!ps) continue;

            // New particles
            var main = ps.main;
            var target = c;
            if (main.startColor.mode == ParticleSystemGradientMode.Color)
            {
                var cur = main.startColor.color;
                target.a = cur.a; // preserve existing alpha behavior
            }
            main.startColor = target;

            // In-flight particles
            var col = ps.colorOverLifetime;
            col.enabled = true;

            // Constant gradient at 'target' (or multiply existing)
            var grad = new Gradient();
            grad.SetKeys(
                new [] { new GradientColorKey(target, 0f), new GradientColorKey(target, 1f) },
                new [] { new GradientAlphaKey(target.a, 0f), new GradientAlphaKey(target.a, 1f) }
            );
            col.color = new ParticleSystem.MinMaxGradient(grad);
        }
    }
    public void ShowBright(Color c)
    {
        SetTintWithParticles(c);
        ToggleRenderers(true);
        ApplyAlpha(1f);
    }
    public void ShowDim(Color _ignored)
    {
        var gray = new Color(0.6f, 0.6f, 0.6f, 1f); // pick your taste
        SetPreviewTint(gray);                       // use neutral, not track color
        ToggleRenderers(true);                      // keep visible while dim
        ApplyAlpha(0.35f);
    }

    void SetTintWithParticles(Color c)
    {
        // Sprites
        var srs = GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < srs.Length; i++) if (srs[i]) srs[i].color = c;

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
                new [] { new GradientAlphaKey(start.a, 0f), new GradientAlphaKey(start.a, 1f) }
            );
            col.color = new ParticleSystem.MinMaxGradient(grad);
        }
    }


    public void HideAll()
    {
        ToggleRenderers(false);
    }

    void ApplyAlpha(float a)
    {
        Debug.Log($"Applying alpha to {a}");
        var srs = GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < srs.Length; i++)
            if (srs[i]) { var col = srs[i].color; col.a = a; srs[i].color = col; }

        var pss = GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < pss.Length; i++)
        {
            var ps = pss[i]; if (!ps) continue;
            var main = ps.main;
            var c = main.startColor.color; c.a = a;
            main.startColor = new ParticleSystem.MinMaxGradient(c);
        }
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
