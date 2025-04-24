using UnityEngine;
using MidiPlayerTK;

public enum CollectionEffectType
{
    // 🌌 New lifecycle-aware types
    PhaseStar,        // Collision with the star itself
    Aether,           // Diamond
    Burst,            // Triangle
    Core              // Hexagon
}

public class CollectionSoundManager : MonoBehaviour
{
    [Header("MIDI Settings")]
    public MidiStreamPlayer midiStreamPlayer;
    public int midiChannel = 0;
    public int defaultFxPreset = 89; // Warm Pad (safe fallback)

    // All presets assumed to be in Bank 0
    private const int fxBank = 128;

    public static CollectionSoundManager Instance;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    public void PlayEffect(CollectionEffectType type, MusicalRole role = MusicalRole.Harmony)
    {
        int note = 72; // Default MIDI note
        int preset = defaultFxPreset; // You can customize per type

        switch (type)
        {
            case CollectionEffectType.PhaseStar:
                note = 96; // High shimmer
                preset = 96; // Sweep Pad or FX
                break;

            case CollectionEffectType.Aether:
                note = 84; // C6
                preset = 88; // Pad 1 (new age)
                break;

            case CollectionEffectType.Burst:
                note = 101; // C4
                preset = 45; // Synth drum
                break;

            case CollectionEffectType.Core:
                note = 48; // C3
                preset = 89; // Warm pad
                break;

            // ...existing TrackUtility effects
        }
        
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedBank = fxBank;
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedPreset = preset;
        midiStreamPlayer.MPTK_PlayEvent(new MPTKEvent()
        {
            Command = MPTKCommand.NoteOn,
            Value = note,
            Channel = midiChannel,
            Duration = 400,
            Velocity = 100
        });
    }

}
