using System.Collections;
using UnityEngine;

[RequireComponent(typeof(MinedObject))]
public class MinedObjectVisualEffectController : MonoBehaviour
{
    public VisualEffectProfile[] roleProfiles;
    public VisualEffectProfile[] utilityProfiles;
    private MinedObject minedObject;
    private NoteSpawnerMinedObject noteSpawner;
    private TrackUtilityMinedObject trackUtility;
    private SpriteRenderer sprite;
    private ParticleSystem particles;
    private VisualEffectProfile activeProfile;

    void Start()
    {
        minedObject = GetComponent<MinedObject>();
        noteSpawner = GetComponent<NoteSpawnerMinedObject>();
        sprite = minedObject?.sprite;

        if (noteSpawner != null)
        {
            activeProfile = GetProfileForRole(noteSpawner.musicalRole);
        }
        else if (trackUtility != null)
        {
            activeProfile = GetProfileForUtility(trackUtility.type);
        }
        
        if (activeProfile != null)
        {
            ApplyVisuals();
            // Instantiate particle system and set color
            if (activeProfile.particlePrefab != null)
            {
                // Instantiate the full GameObject prefab
                GameObject fxObject = Instantiate(activeProfile.particlePrefab, transform.position, Quaternion.identity, transform);

                // Then get the ParticleSystem from the instantiated object
                particles = fxObject.GetComponent<ParticleSystem>();
                if (particles != null)
                {
                    var main = particles.main;
                    if (sprite.color != null)
                    {
                        main.startColor = sprite.color;
                    }

                    if (trackUtility != null)
                    {
                        ConfigureParticlesForUtilityType(trackUtility.type);
                    }
                    particles.Play();
                }
                else
                {
                    Debug.LogWarning($"No ParticleSystem found on particleEffectPrefab '{activeProfile.particlePrefab.name}'");
                }
            }

            StartCoroutine(PulseLoop());
        }
    }
    private void ApplyVisuals()
    {
        if (sprite == null || activeProfile == null) return;
        
        // Set sprite color to match the glow color (or blend with track color)
        sprite.color = activeProfile.glowColor;

        // Set glow color in material, if supported
        if (sprite.material.HasProperty("_GlowColor"))
        {
            Color glow = activeProfile.glowColor;
            glow.a = 0.3f;
            sprite.material.SetColor("_GlowColor", glow);
        }

        // Optionally apply any other initialization (e.g. flicker, trail setup)
    }

    private void ConfigureParticlesForUtilityType(TrackModifierType type)
    {
        if (particles == null) return;

        var main = particles.main;
        var shape = particles.shape;
        var emission = particles.emission;
        var renderer = particles.GetComponent<ParticleSystemRenderer>();

        switch (type)
        {
            case TrackModifierType.StructureShift:
                // ⬢ Sharp, geometric, structured
                main.startColor = Color.cyan;
                main.startSize = 0.2f;
                main.startLifetime = 1f;
                main.startSpeed = 1.2f;

                shape.shapeType = ParticleSystemShapeType.Box;
                shape.scale = new Vector3(0.5f, 0.5f, 0.1f);

                emission.rateOverTime = 10;
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                break;

            case TrackModifierType.MoodShift:
                // 🌊 Soft, flowing, emotional
                main.startColor = new Color(1f, 0.7f, 0.9f); // pastel rose
                main.startSize = 0.15f;
                main.startLifetime = 2f;
                main.startSpeed = 0.2f;

                shape.shapeType = ParticleSystemShapeType.Donut;
                shape.radius = 0.3f;
                shape.arcMode = ParticleSystemShapeMultiModeValue.Loop;

                emission.rateOverTime = 20;
                renderer.renderMode = ParticleSystemRenderMode.Stretch;
                renderer.lengthScale = 1.2f;
                break;

            default:
                Debug.Log($"No particle override defined for {type}");
                break;
        }
    }

    void Update()
    {
        if (activeProfile != null)
        {
            transform.Rotate(Vector3.forward, activeProfile.rotationSpeed * Time.deltaTime);
        }
    }

    private IEnumerator PulseLoop()
    {
        while (true)
        {
            float pulse = (Mathf.Sin(Time.time * activeProfile.pulseSpeed) + 1f) / 2f;
            float scale = Mathf.Lerp(1f, activeProfile.pulseScaleAmount, pulse);
            transform.localScale = Vector3.one * scale;

            if (sprite != null && sprite.material.HasProperty("_GlowColor"))
            {
                Color glow = activeProfile.glowColor;
                glow.a = Mathf.Lerp(0.2f, 0.4f, pulse);
                sprite.material.SetColor("_GlowColor", glow);
            }

            yield return null;
        }
    }

    private VisualEffectProfile GetProfileForRole(MusicalRole role)
    {
        foreach (var profile in roleProfiles)
        {
            if (profile != null && profile.role == role)
                return profile;
        }
        Debug.LogWarning($"No visual profile found for role: {role}");
        return null;
    }
    private VisualEffectProfile GetProfileForUtility(TrackModifierType type)
    {
        foreach (var profile in utilityProfiles)
        {
            if (profile != null && profile.utilityType == type)
                return profile;
        }

        Debug.LogWarning($"No visual profile found for utility type: {type}");
        return null;
    }

    public void PlayCollectEffect()
    {
        if (particles != null)
        {
            particles.Play();
        }
    }
}
