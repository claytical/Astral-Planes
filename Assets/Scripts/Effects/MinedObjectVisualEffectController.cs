using System;
using System.Collections;
using UnityEditor.UI;
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

    public GameObject Initialize(NoteSpawnerMinedObject spawner)
    {
        minedObject = GetComponent<MinedObject>();
        noteSpawner = spawner;
        sprite = minedObject?.sprite;

        if (noteSpawner != null)
        {
            activeProfile = GetProfileForRole(noteSpawner.musicalRole.role);
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
                spawner.ghostTrigger = Instantiate(activeProfile.particlePrefab, transform.position, Quaternion.identity, transform);
                GhostPatternTrigger trigger = spawner.ghostTrigger.GetComponent<GhostPatternTrigger>();
                if (trigger != null)
                {
                    if (noteSpawner != null)
                        trigger.Initialize(noteSpawner.selectedNoteSet, noteSpawner.assignedTrack,
                            noteSpawner.musicalRole);
                }
                // Then get the ParticleSystem from the instantiated object
                particles = spawner.ghostTrigger.GetComponent<ParticleSystem>();
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

                return spawner.ghostTrigger;
            }
        }

        return null;
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

    private void OnEnable()
    {
     //   StartCoroutine(PulseLoop());
    }

    private IEnumerator PulseLoop()
    {
        while (gameObject.activeSelf) ;
        {
            float pulse = (Mathf.Sin(Time.time * activeProfile.pulseSpeed) + 1f) / 2f;
            float scale = Mathf.Lerp(1f, activeProfile.pulseScaleAmount, pulse);
            transform.localScale = Vector3.one * scale;
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

}
