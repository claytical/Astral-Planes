using System;
using System.Collections;
using UnityEngine;

public class GhostPatternTrigger : MonoBehaviour
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
                Debug.Log($"Starting Astral Transfer...");
                StartCoroutine(HandleAstralTransfer(vehicle));
            }
            else
            {
                Debug.Log($"Astral Transfer Aborted");
            }
            hasBeenTriggered = true;
        }

    }

    private void PlayEnvelopePulse()
    {
        if (selectedMusicalRoleProfile == null || selectedMusicalRoleProfile.envelopeParticlePrefab == null) return;

        GameObject pulse = Instantiate(selectedMusicalRoleProfile.envelopeParticlePrefab, transform.position, Quaternion.identity);
    
        var ps = pulse.GetComponent<ParticleSystem>();
        if (ps != null)
        {
            var main = ps.main;
            main.startColor = selectedMusicalRoleProfile.defaultColor;
            ps.Play();
        }

        Destroy(pulse, 2f);
    }

    private IEnumerator HandleAstralTransfer(Vehicle vehicle)
    {
        // 🛑 Early exit if setup is invalid
        if (selectedInstrument == null || selectedNoteSet == null)
        {
            Debug.LogWarning("❌ Astral transfer failed — missing InstrumentTrack or NoteSet.");
            yield break;
        }

        // ✨ 1. Trigger vehicle color flicker & pulse
        if (vehicle != null)
        {
            Color pulseColor = selectedInstrument.trackColor;
            vehicle.TriggerFlickerAndPulse(0.5f, pulseColor);
        }

        // 💨 2. Play gas cloud pulse
        PlayEnvelopePulse();

        // 👻 3. Spawn the ghost pattern
        Vector3 spawnPosition = transform.position;
        GameObject ghostObj = Instantiate(vehicle.ghostVehiclePrefab, spawnPosition, Quaternion.identity);

        if (!ghostObj.TryGetComponent(out GhostPattern ghost))
        {
            Debug.LogError("❌ Ghost prefab missing GhostPattern component.");
            yield break;
        }

        // ⚡ 4. Grant vehicle energy and start the ghost routine
        vehicle.CollectEnergy(10);
        ghost.Initialize(vehicle, selectedNoteSet, selectedInstrument, selectedMusicalRoleProfile);
        ghost.Create();

        // 🌫 5. Remove or fade out this trigger object
        if (TryGetComponent(out Explode explode))
        {
            explode.Permanent(); // fade/poof
        }
        else
        {
            Destroy(gameObject); // fallback
        }

        yield return null;
    }

}
