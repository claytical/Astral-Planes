using System;
using System.Collections;
using UnityEngine;

public class TrackUtilityTrigger : MonoBehaviour
{
    public NoteSet selectedNoteSet;
    public InstrumentTrack selectedInstrument;
    public MusicalRoleProfile selectedMusicalRoleProfile;
    private bool hasBeenTriggered = false;
    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    public void Initialize(NoteSet noteSet, InstrumentTrack instrument, MusicalRoleProfile profile)
    {
        selectedNoteSet = noteSet;
        selectedInstrument = instrument;
        selectedMusicalRoleProfile = profile;
    }

    // Update is called once per frame
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
