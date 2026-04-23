using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;


public class Explode : MonoBehaviour
{
    public GameObject explosion;
    public GameObject preExplosion;
    public float lifetime;
    public bool randomizeLifetimer = false;
    private float explosionTimer;
    [Header("Tint")]
    [SerializeField] private bool tintExplosions = true;
    [SerializeField] private Color explosionTint = Color.white;

    // If true, multiply the existing authored colors by explosionTint.
    // If false, override to explosionTint (alpha still respects authored alpha if possible).
    [SerializeField] private bool multiplyWithAuthoredColor = true;

    // Also apply tint to particle trails when present
    [SerializeField] private bool tintTrails = true;


    public void SetTint(Color tint, bool multiply = true)
{
    explosionTint = tint;
    multiplyWithAuthoredColor = multiply;
}

    private Vector2 _burstDir = Vector2.zero;

    public void SetBurstDirection(Vector2 worldDir)
    {
        _burstDir = worldDir.sqrMagnitude > 0.0001f ? worldDir.normalized : Vector2.zero;
    }

    private void ApplyDirectionToInstance(GameObject instance, Vector2 dir)
    {
        if (instance == null || dir.sqrMagnitude < 0.0001f) return;

        float angleDeg = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        var systems = instance.GetComponentsInChildren<ParticleSystem>(true);
        foreach (var ps in systems)
        {
            // Rotate the particle system transform so the shape cone faces the burst direction.
            ps.transform.rotation = Quaternion.Euler(0f, 0f, angleDeg - 90f);

            // Bias velocity over lifetime along the burst direction.
            var vol = ps.velocityOverLifetime;
            if (vol.enabled)
            {
                vol.space = ParticleSystemSimulationSpace.World;
                float spd = vol.x.constant;
                float mag = Mathf.Max(Mathf.Abs(spd), 1f);
                vol.x = new ParticleSystem.MinMaxCurve(dir.x * mag);
                vol.y = new ParticleSystem.MinMaxCurve(dir.y * mag);
            }
        }
    }

    private void ApplyTintToInstance(GameObject instance, Color tint)
{
    if (!tintExplosions) return;
    if (instance == null) return;

    var systems = instance.GetComponentsInChildren<ParticleSystem>(true);
    for (int i = 0; i < systems.Length; i++)
        ApplyTintToParticleSystem(systems[i], tint);
}

    private void ApplyTintToParticleSystem(ParticleSystem ps, Color tint)
    {
        if (ps == null) return;

        var main = ps.main;
        var visibleTint = tint;
        visibleTint.a = 1f;
        main.startColor = visibleTint;
        var col = ps.colorOverLifetime;
        if (col.enabled)
        {
            // Build a predictable white-to-transparent fade.
            // startColor already carries the role tint; colorOverLifetime only needs to
            // control the alpha fade so particles are always visible and always dissipate.
            var fadeGrad = new Gradient();
            fadeGrad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) }
            );
            col.color = new ParticleSystem.MinMaxGradient(fadeGrad);
        }

        if (tintTrails)
        {
            var trails = ps.trails;
            if (trails.enabled)
                trails.colorOverLifetime = TintMinMaxGradient(trails.colorOverLifetime, tint, multiplyWithAuthoredColor);
        }
    }

    private static ParticleSystem.MinMaxGradient TintMinMaxGradient(
        ParticleSystem.MinMaxGradient src,
        Color tint,
        bool multiply)
    {
        switch (src.mode)
        {
            case ParticleSystemGradientMode.Color:
            {
                Color c = src.color;
                src.color = multiply ? (c * tint) : CombineOverridePreserveAlpha(c, tint);
                return src;
            }

            case ParticleSystemGradientMode.TwoColors:
            {
                Color c0 = src.colorMin;
                Color c1 = src.colorMax;
                src.colorMin = multiply ? (c0 * tint) : CombineOverridePreserveAlpha(c0, tint);
                src.colorMax = multiply ? (c1 * tint) : CombineOverridePreserveAlpha(c1, tint);
                return src;
            }

            case ParticleSystemGradientMode.Gradient:
            {
                Gradient g = src.gradient;
                src.gradient = TintGradient(g, tint, multiply);
                return src;
            }

            case ParticleSystemGradientMode.TwoGradients:
            {
                Gradient g0 = src.gradientMin;
                Gradient g1 = src.gradientMax;
                src.gradientMin = TintGradient(g0, tint, multiply);
                src.gradientMax = TintGradient(g1, tint, multiply);
                return src;
            }

            case ParticleSystemGradientMode.RandomColor:
            {
                Gradient g = src.gradient;
                src.gradient = TintGradient(g, tint, multiply);
                return src;
            }

            default:
                return src;
        }
    }

    private static Color CombineOverridePreserveAlpha(Color authored, Color tint)
    {
        return new Color(tint.r, tint.g, tint.b, authored.a * tint.a);
    }

    private static Gradient TintGradient(Gradient g, Color tint, bool multiply)
    {
        if (g == null) return null;

        var ck = g.colorKeys;
        for (int i = 0; i < ck.Length; i++)
        {
            Color c = ck[i].color;
            ck[i].color = multiply ? (c * tint) : CombineOverridePreserveAlpha(c, tint);
        }

        var ak = g.alphaKeys;
        for (int i = 0; i < ak.Length; i++)
            ak[i].alpha = Mathf.Clamp01(ak[i].alpha * tint.a);

        // Guarantee the gradient fully dissipates when the authored tail reaches end-of-life.
        // Requires at least 2 keys so we don't zero the only alpha key (which would hide
        // particles entirely for their whole lifetime).
        if (ak.Length >= 2 && ak[ak.Length - 1].time >= 0.99f)
            ak[ak.Length - 1].alpha = 0f;

        var ng = new Gradient();
        ng.SetKeys(ck, ak);
        return ng;
    }

    void Start()
    {
        if (randomizeLifetimer)
        {
            explosionTimer = Time.time + Random.Range(0f, lifetime);
        }
        else
        {
            explosionTimer = Time.time + lifetime;
        }
    }

    void Update()
    {
        if (lifetime > 0)
        {
            if (Time.time > explosionTimer)
            {
                Permanent();
                lifetime = 0;
            }
        }
    }

    public void PreExplode()
    {
        if (preExplosion == null)
        {
            Debug.LogWarning($"[EXPLODE] PreExplode skipped: preExplosion is null on {name}", this);
            return;
        }

        Debug.Log("[EXPLODE] PreExplode");
        var go = Instantiate(preExplosion, transform.position, Quaternion.identity);
        ApplyTintToInstance(go, explosionTint);
        if (_burstDir.sqrMagnitude > 0.0001f)
            ApplyDirectionToInstance(go, _burstDir);
    }


    public void Permanent(bool permanent = true)
    {
        if (GetComponent<Vehicle>())
        { 
            Destroy(gameObject, 1);
            return;
        }

        if (GetComponent<Rigidbody2D>())
        {
            GetComponent<Rigidbody2D>().linearVelocity = Vector2.zero;
        }

        if (explosion != null)
        {
            var go = Instantiate(explosion, transform.position, Quaternion.identity);
            ApplyTintToInstance(go, explosionTint);
            if (_burstDir.sqrMagnitude > 0.0001f)
                ApplyDirectionToInstance(go, _burstDir);
        }


        if (permanent)
        {
            Destroy(this.gameObject);
        }
    }


}
