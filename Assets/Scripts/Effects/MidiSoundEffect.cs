using UnityEngine;

[RequireComponent(typeof(Collider2D))]
public class MIDISoundEffect : MonoBehaviour
{
    public CollectionEffectType effectType;

    [Tooltip("Only used for Role-based effects like LoopExpansion and TrackClear")]
    public MusicalRole musicalRole = MusicalRole.Harmony;

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (other.GetComponent<Vehicle>() && CollectionSoundManager.Instance != null)
        {
            if (effectType == CollectionEffectType.DrumLoopPattern)
            {
                TryPlayDrumPatternEffect();
            }
            else
            {
                Debug.Log($"🔊 Playing {effectType} effect with role: {musicalRole}");
                CollectionSoundManager.Instance.PlayEffect(effectType, musicalRole);
            }
        }
    }

    private void TryPlayDrumPatternEffect()
    {
        DrumLoopCollectable collectable = GetComponent<DrumLoopCollectable>();
        if (collectable == null)
        {
            Debug.LogWarning("MIDISoundEffect: No DrumLoopCollectable found for DrumLoopPattern sound.");
            return;
        }

        DrumLoopPattern pattern = collectable.pattern;
        int note = 84;
        int preset = 98; // Default fallback

        switch (pattern)
        {
            case DrumLoopPattern.Establish:
                note = 72; // Lower octave
                preset = 89; // Warm Pad
                break;
            case DrumLoopPattern.Evolve:
                note = 74;
                preset = 91; // Space Voice
                break;
            case DrumLoopPattern.Intensify:
                note = 76;
                preset = 94; // Halo Pad
                break;
            case DrumLoopPattern.Release:
                note = 77;
                preset = 95; // Sweep Pad
                break;
            case DrumLoopPattern.Wildcard:
                note = 78;
                preset = 102; // Echo Drops
                break;
            case DrumLoopPattern.Pop:
                note = 79;
                preset = 99; // Atmosphere
                break;
        }

        Debug.Log($"🔊 Freeplay pad triggered for pattern '{pattern}': Note={note}, Preset={preset}");
        CollectionSoundManager.Instance.PlayNoteEffect(note, 100, 300, preset);
    }
}
