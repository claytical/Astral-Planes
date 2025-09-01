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
    private void DisableTriggerVisualsAndColliders()
    {
        // 1) Stop & clear EVERY particle under this trigger
        foreach (var ps in GetComponentsInChildren<ParticleSystem>(true))
        {
            var emission = ps.emission; emission.enabled = false;
            ps.Stop(withChildren: true, stopBehavior: ParticleSystemStopBehavior.StopEmittingAndClear);
            var renderer = ps.GetComponent<Renderer>();
            if (renderer) renderer.enabled = false;
        }

        // 2) Disable any Sprite/Mesh renderers just in case
        foreach (var r in GetComponentsInChildren<Renderer>(true))
            r.enabled = false;

        // 3) Disable colliders so it can’t be triggered again
        foreach (var col in GetComponentsInChildren<Collider2D>(true))
            col.enabled = false;
    }

    private IEnumerator PoofThenDispose(float delayBeforeDispose = 0.05f, bool usePooling = false)
    {
        // optional: play your explode effect
        if (TryGetComponent(out Explode explode))
            explode.Permanent();

        // give the poof a heartbeat if desired
        if (delayBeforeDispose > 0f) yield return new WaitForSeconds(delayBeforeDispose);

        if (usePooling) gameObject.SetActive(false);
        else Destroy(gameObject);
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

// 🌫 Immediately hide trigger visuals so nothing lingers
        DisableTriggerVisualsAndColliders();

// (Optional) play poof, then remove the trigger object
        StartCoroutine(PoofThenDispose(0.05f /* small beat */, usePooling: false));
        
        yield return null;
    }

}
