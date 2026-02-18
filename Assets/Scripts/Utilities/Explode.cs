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
    main.startColor = tint;

    var col = ps.colorOverLifetime;
    if (col.enabled)
        col.color = TintMinMaxGradient(tint, Color.white, multiplyWithAuthoredColor);

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

    public void ApplyLifetimeProfile(LifetimeProfile profile)
    {
        if (profile == null) return;

        lifetime = profile.lifetime;
        randomizeLifetimer = profile.randomizeLifetime;

        if (randomizeLifetimer)
        {
            explosionTimer = Time.time + Random.Range(0f, lifetime);
        }
        else
        {
            explosionTimer = Time.time + lifetime;
        }
    }

    public void DelayedExplosion(float delay = 0.1f)
    {
        Invoke(nameof(Permanent), delay);
    }

    public void PreExplosion()
    {
        if (preExplosion != null)
        {
            var go = Instantiate(preExplosion, transform.position, Quaternion.identity);
            ApplyTintToInstance(go, explosionTint);
        }
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
        }


        if (permanent)
        {
            Destroy(this.gameObject);
        }
    }


}
