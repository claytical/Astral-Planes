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
    public delegate void OnCollectedHandler(int duration);

    public bool easingComplete = false;
    public event OnCollectedHandler OnCollected;
    public event Action<Collectable> OnDestroyed;
    public void Initialize(int note, InstrumentTrack track)
    {
        assignedNote = note;
        assignedInstrumentTrack = track; // ✅ Store reference for validation
    }

    private void OnTriggerEnter2D(Collider2D coll)
    {

        if (coll.gameObject.GetComponent<Vehicle>())
        {

            coll.gameObject.GetComponent<Vehicle>().CollectEnergy(amount);
            OnCollected?.Invoke(noteDurationTicks); // Pass duration when collected

            var explode = GetComponent<Explode>();
            if (explode != null)
            {
                explode.Permanent();  // Destroy the collectable with an effect
            }
        }
    }
}
