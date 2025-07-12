using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Collectable : MonoBehaviour
{
    public InstrumentTrack assignedInstrumentTrack; // 🎼 The track that spawned this collectable
    public SpriteRenderer energySprite;
    private int amount = 1;
    private int noteDurationTicks = 4; // 🎵 Default to 1/16th note duration
    private int assignedNote; // 🎵 The MIDI note value
    private Vector3 startPosition;
    private ParticleSystem particleSystem;
    public delegate void OnCollectedHandler(int duration, float force);
    public event OnCollectedHandler OnCollected;
    public event Action OnDestroyed;

    // 🔹 Initializes the collectable with its note data
    public void Initialize(int note, int duration, InstrumentTrack track, NoteSet noteSet)
    {
        assignedNote = note;
        noteDurationTicks = duration;
        assignedInstrumentTrack = track;
        CollectableParticles particleScript = GetComponent<CollectableParticles>();
        if (particleScript != null && noteSet != null)
        {
            particleScript.Configure(noteSet);
        }

        energySprite.color = track.trackColor;

        if (assignedInstrumentTrack == null)
        {
            Debug.LogError($"Collectable {gameObject.name} - assignedInstrumentTrack is NULL on initialization!");
        }
        var explode = GetComponent<Explode>();
        if (explode != null)
        {
            explode.ApplyLifetimeProfile(LifetimeProfile.GetProfile(MinedObjectType.Note));
        }        
    }

    public int GetNote()
    {
        return assignedNote;
    }
    private void OnTriggerEnter2D(Collider2D coll)
    {
        Vehicle vehicle = coll.gameObject.GetComponent<Vehicle>();
        if (vehicle)
        {
            Debug.Log($"Collected object {coll.gameObject.name}");
            float force = vehicle.GetForceAsMidiVelocity(); // 60–127
          //  float normalizedMIDI = (force - 40f) / (127f - 40f);
            int energyReward = Mathf.RoundToInt(Mathf.Lerp(6f, 20f, (force - 40f) / 87f));

            vehicle.TriggerFlickerAndPulse(1.1f, energySprite.color, false);
            vehicle.CollectEnergy(energyReward);
            
            // 🔹 Play the note immediately
            OnCollected?.Invoke(noteDurationTicks, force);
            if (assignedInstrumentTrack == null)
            {
                Debug.LogError($"{gameObject.name} - assignedInstrumentTrack is NULL on collection!");
                return;
            }
            // 🔹 Trigger destruction effect
            var explode = GetComponent<Explode>();
            if (explode != null)
            {
                OnDestroyed?.Invoke();
                explode.Permanent(); // Destroy the collectable with a visual effect
            }
            else
            {
                OnDestroyed?.Invoke();
                Destroy(gameObject);
            }
        }
    }
    private IEnumerator FadeOutAndDestroy()
    {
        SpriteRenderer sr = energySprite;
        Color original = sr.color;
        float duration = 0.6f;

        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            float alpha = Mathf.Lerp(original.a, 0f, t / duration);
            sr.color = new Color(original.r, original.g, original.b, alpha);
            yield return null;
        }

        OnDestroyed?.Invoke();
        Destroy(gameObject);
    }

}
