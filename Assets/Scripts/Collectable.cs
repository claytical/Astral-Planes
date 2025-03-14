using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Collectable : MonoBehaviour
{
    public int amount = 1;
    public int noteDurationTicks = 4; // 🎵 Default to 1/16th note duration
    public int assignedNote; // 🎵 The MIDI note value
    public InstrumentTrack assignedInstrumentTrack; // 🎼 The track that spawned this collectable
    public NoteBehavior noteBehavior; // 🎵 NEW: Stores the note’s behavior

    public delegate void OnCollectedHandler(int duration, float force);
    public event OnCollectedHandler OnCollected;
    public event System.Action OnDestroyed;

    public float floatAmplitude = 0.2f; // 🔄 Floating effect size
    public float floatSpeed = 2f; // 🔄 Floating speed
    public bool easingComplete = false; // ✅ Allows floating after easing-in
    private Vector3 startPosition;
    private float floatTimer = 0f; 

    // 🔹 Initializes the collectable with its note data
    public void Initialize(int note, int duration, InstrumentTrack track, NoteBehavior behavior)
    {
        assignedNote = note;
        noteDurationTicks = duration;
        assignedInstrumentTrack = track;
        noteBehavior = behavior;

        if (assignedInstrumentTrack == null)
        {
            Debug.LogError($"Collectable {gameObject.name} - assignedInstrumentTrack is NULL on initialization!");
        }
    }

    private void Update()
    {
        if (easingComplete)
        {
            // 🔄 Apply sine wave motion for floating
            float newY = startPosition.y + Mathf.Sin(floatTimer * floatSpeed) * floatAmplitude;
            transform.position = new Vector3(startPosition.x, newY, startPosition.z);
            
            // Increment float time
            floatTimer += Time.deltaTime;
        }
    }

    private void OnTriggerEnter2D(Collider2D coll)
    {
        Vehicle vehicle = coll.gameObject.GetComponent<Vehicle>();
        if (vehicle)
        {
            vehicle.CollectEnergy(amount);
            Debug.Log($"{gameObject.name} collected energy");
            // 🔹 Play the note immediately
            OnCollected?.Invoke(noteDurationTicks, vehicle.GetForce());
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
