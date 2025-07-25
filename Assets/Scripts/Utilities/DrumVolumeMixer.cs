using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class InfluenceToggle
{
    public DrumVolumeInfluence influence;
    public bool enabled = true;
}

public class DrumVolumeMixer : MonoBehaviour
{
    public DrumTrack drumTrack;
    public List<InfluenceToggle> influenceToggles = new();
    public float currentVolume = 0f;
    public float smoothingSpeed = 1.5f;

    private AudioSource audioSource;

    void Start()
    {
        audioSource = drumTrack?.drumAudioSource;
    }

    void Update()
    {
        float blended = 0f;
        float totalWeight = 0f;

        foreach (var toggle in influenceToggles)
        {
            if (!toggle.enabled || toggle.influence == null) continue;

            float contribution = toggle.influence.EvaluateVolume(drumTrack);
            blended += contribution * toggle.influence.weight;
            blended *= 3;
            totalWeight += toggle.influence.weight;
        }

        if (totalWeight > 0f) blended /= totalWeight;

        currentVolume = Mathf.Lerp(currentVolume, blended, Time.deltaTime * smoothingSpeed);
        if (audioSource != null)
        {
            audioSource.volume = currentVolume;
        }
    }
}