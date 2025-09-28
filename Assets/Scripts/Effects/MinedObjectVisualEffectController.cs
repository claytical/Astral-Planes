using System;
using System.Collections;
using Gameplay.Mining;
using UnityEditor.UI;
using UnityEngine;

[RequireComponent(typeof(MinedObject))]
public class MinedObjectVisualEffectController : MonoBehaviour
{
    public VisualEffectProfile[] roleProfiles;
    private MinedObject minedObject;
    private NoteSpawnerMinedObject noteSpawner;
    private TrackUtilityMinedObject trackUtility;
    private SpriteRenderer sprite;
    private ParticleSystem particles;
    private VisualEffectProfile activeProfile;

    public void Initialize(MinedObject minedObject)
    {
        trackUtility = minedObject.GetComponent<TrackUtilityMinedObject>();
        noteSpawner = minedObject.GetComponent<NoteSpawnerMinedObject>();
        sprite = minedObject?.sprite;
        activeProfile = GetProfileForRole(minedObject.musicalRole);
        if (activeProfile != null)
        {
            ApplyVisuals();
            // Instantiate particle system and set color
            if (activeProfile.particlePrefab != null)
            {
                
                // Then get the ParticleSystem from the instantiated object
                particles = minedObject.GetComponent<ParticleSystem>();
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
            }
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
            case TrackModifierType.RootShift:
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

            case TrackModifierType.ChordProgression:
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

}
