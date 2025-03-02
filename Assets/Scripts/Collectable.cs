using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
public class Collectable : MonoBehaviour
{
    public int amount = 1;
    public int noteDurationTicks = 4; // Default to a 1/16th note duration (adjustable)
    public int assignedNote; // ✅ Stores the note value
    public InstrumentTrack assignedInstrumentTrack; // ✅ Links to the track that spawned it
    public delegate void OnCollectedHandler(int duration, float force);

    public float floatAmplitude = 0.2f; // Adjust for bigger or smaller movement
    public float floatSpeed = 2f;       // Controls how fast the floating happens
    public bool easingComplete = false;
    private Vector3 startPosition;
    private float floatTimer = 0f;    public event OnCollectedHandler OnCollected;
    public void Initialize(int note, InstrumentTrack track)
    {
        startPosition = transform.position;
        assignedNote = note;
        assignedInstrumentTrack = track; // ✅ Store reference for validation
    }
    private void Update()
    {
        if (easingComplete)
        {
            // Apply sine wave motion to Y-position
            float newY = startPosition.y + Mathf.Sin(floatTimer * floatSpeed) * floatAmplitude;
            transform.position = new Vector3(startPosition.x, newY, startPosition.z);
            
            // Increment time
            floatTimer += Time.deltaTime;
        }
    }
    private void OnTriggerEnter2D(Collider2D coll)
    {
        Vehicle vehicle = coll.gameObject.GetComponent<Vehicle>();
        if (vehicle)
        {

            vehicle.CollectEnergy(amount);
            OnCollected?.Invoke(noteDurationTicks,vehicle.GetForce()); // Pass duration when collected

            var explode = GetComponent<Explode>();
            if (explode != null)
            {
                explode.Permanent();  // Destroy the collectable with an effect
            }
        }
    }
}
