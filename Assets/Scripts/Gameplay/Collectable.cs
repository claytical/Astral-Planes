using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Collectable : MonoBehaviour
{
    public InstrumentTrack assignedInstrumentTrack; // 🎼 The track that spawned this collectable
    public SpriteRenderer energySprite;

//    public float floatAmplitude = 0.2f; // 🔄 Floating effect size
//    public float floatSpeed = 2f; // 🔄 Floating speed
//    public bool easingComplete = false; // ✅ Allows floating after easing-in
    private int amount = 1;
    private int noteDurationTicks = 4; // 🎵 Default to 1/16th note duration
    private int assignedNote; // 🎵 The MIDI note value
    private NoteBehavior noteBehavior; // 🎵 NEW: Stores the note’s behavior
    private Vector3 startPosition;
    private float floatTimer = 0f; 
    private ParticleSystem particleSystem;
    public delegate void OnCollectedHandler(int duration, float force);
    public event OnCollectedHandler OnCollected;
    public event System.Action OnDestroyed;

    // 🔹 Initializes the collectable with its note data
    public void Initialize(int note, int duration, InstrumentTrack track, NoteBehavior behavior)
    {
        assignedNote = note;
        noteDurationTicks = duration;
        assignedInstrumentTrack = track;
        noteBehavior = behavior;
        if (GetComponent<ParticleSystem>())
        {
            ParticleSystem.MainModule particleSystem = GetComponent<ParticleSystem>().main;
            particleSystem.startColor = track.trackColor;
        }
        energySprite.color = track.trackColor;

        if (assignedInstrumentTrack == null)
        {
            Debug.LogError($"Collectable {gameObject.name} - assignedInstrumentTrack is NULL on initialization!");
        }
    }

    public int GetNote()
    {
        return assignedNote;
    }

    public void SetBehavior(NoteBehavior behavior)
    {
        noteBehavior = behavior;        
    }

    private void OnTriggerEnter2D(Collider2D coll)
    {
        Vehicle vehicle = coll.gameObject.GetComponent<Vehicle>();
        if (vehicle)
        {
            vehicle.CollectEnergy(amount);
            Debug.Log($"{gameObject.name} collected energy");
            // 🔹 Play the note immediately
            OnCollected?.Invoke(noteDurationTicks, vehicle.GetForceAsMidiVelocity());
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
        Debug.Log("Checking Section Complete...");
//        assignedInstrumentTrack?.CheckSectionComplete();
    }
}
