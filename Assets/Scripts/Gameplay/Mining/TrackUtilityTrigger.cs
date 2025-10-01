using System;
using System.Collections;
using UnityEngine;
//TODO: Implement Ring utilities for chord progressions

public class TrackUtilityTrigger : MonoBehaviour
{
    public NoteSet selectedNoteSet;
    public InstrumentTrack selectedInstrument;
    private bool hasBeenTriggered = false;
    
    private void OnTriggerEnter2D(Collider2D other)
    {
        if (hasBeenTriggered) return;
        Vehicle vehicle = other.GetComponent<Vehicle>();
        if (vehicle != null)
        {
            CollectionSoundManager.Instance.PlayEffect(SoundEffectPreset.Bloom);
            if (selectedInstrument != null && selectedNoteSet != null)
            {
                Debug.Log($"Track Utility Engaged for {selectedNoteSet.name}...");
                
            }
            else
            {
                Debug.Log($"Track Utility Aborted");
            }
            hasBeenTriggered = true;
        }

    }

}
