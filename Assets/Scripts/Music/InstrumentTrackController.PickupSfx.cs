using UnityEngine;
using Random = UnityEngine.Random;

// SFX: Collection "Pickup Tick" (A2)
public partial class InstrumentTrackController
{
    private void EnsurePickupSfxSource()
    {
        if (pickupSfxSource != null) return;

        // Dedicated 2D one-shot source (never touches drum loop sources).
        var go = new GameObject("TrackPickupSFX");
        go.transform.SetParent(transform, worldPositionStays: false);

        pickupSfxSource = go.AddComponent<AudioSource>();
        pickupSfxSource.playOnAwake = false;
        pickupSfxSource.loop = false;
        pickupSfxSource.spatialBlend = 0f; // 2D
        pickupSfxSource.volume = 1f;       // volume scaled per PlayOneShot call
    }

    private AudioClip GetPickupTickClip(MusicalRole role)
    {
        switch (role)
        {
            case MusicalRole.Bass:    return config.pickupTickBass    != null ? config.pickupTickBass    : config.pickupTickDefault;
            case MusicalRole.Lead:    return config.pickupTickLead    != null ? config.pickupTickLead    : config.pickupTickDefault;
            case MusicalRole.Harmony: return config.pickupTickHarmony != null ? config.pickupTickHarmony : config.pickupTickDefault;
            case MusicalRole.Groove:  return config.pickupTickGroove  != null ? config.pickupTickGroove  : config.pickupTickDefault;
            default:                  return config.pickupTickDefault;
        }
    }

    private void PlayPickupTick(InstrumentTrack track)
    {
        if (track == null) return;

        EnsurePickupSfxSource();
        if (pickupSfxSource == null) return;

        var clip = GetPickupTickClip(track.assignedRole);
        if (clip == null) return; // nothing configured yet

        float prevPitch = pickupSfxSource.pitch;
        if (config.pickupTickPitchJitter > 0f)
            pickupSfxSource.pitch = 1f + Random.Range(-config.pickupTickPitchJitter, config.pickupTickPitchJitter);

        pickupSfxSource.PlayOneShot(clip, config.pickupTickVolume);
        pickupSfxSource.pitch = prevPitch;
    }

    public void NotifyCollected(InstrumentTrack track)
    {
        PlayPickupTick(track);
    }
}
