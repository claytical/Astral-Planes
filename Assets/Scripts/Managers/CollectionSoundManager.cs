using System.Collections.Generic;
using UnityEngine;
using MidiPlayerTK;

public enum CollectionEffectType
{
    // 🌌 New lifecycle-aware types
    PhaseStar,        // Collision with the star itself
    Aether,           // Diamond
    Burst,            // Triangle
    Core,              // Hexagon
    MazeToxic,
    MazeFriendly
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
            case CollectionEffectType.MazeFriendly:
                TinklePopSound pop = FriendlyNoteUtility.GetTinklePopSound(60);
                note = pop.note;
                preset = pop.preset; // always 112

                break;
            case CollectionEffectType.MazeToxic:
                ToxicSound toxic = ToxicNoteUtility.GetToxicSound(60);
                note = toxic.note;
                preset = toxic.preset;
                break;
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

public struct TinklePopSound
{
    public int note;
    public int preset;

    public TinklePopSound(int note, int preset)
    {
        this.note = note;
        this.preset = preset;
    }
}

public static class FriendlyNoteUtility
{
    // C major scale intervals (can be reused across keys)
    private static readonly int[] MajorScaleOffsets = { 0, 2, 4, 5, 7, 9, 11, 12 };

    public static TinklePopSound GetTinklePopSound(int rootNote = 60)
    {
        int interval = MajorScaleOffsets[UnityEngine.Random.Range(0, MajorScaleOffsets.Length)];
        int note = Mathf.Clamp(rootNote + interval, 48, 84);
        return new TinklePopSound(note, 112); // Always Tinkle Bell
    }
}

public struct ToxicSound
{
    public int note;
    public int preset;

    public ToxicSound(int note, int preset)
    {
        this.note = note;
        this.preset = preset;
    }
}

public static class ToxicNoteUtility
{
    // Diatonic major scale intervals to avoid
    private static readonly int[] MajorScaleOffsets = { 0, 2, 4, 5, 7, 9, 11 };

    // Dissonant or chromatic intervals
    private static readonly int[] DissonantOffsets = { 1, 3, 6, 8, 10, 13, 14 };

    // Gritty or eerie-sounding General MIDI preset IDs
    private static readonly int[] DarkPresets = {
        38, // Synth Bass 1
        39 // Synth Bass 2
    };

    public static ToxicSound GetToxicSound(int rootNote = 60)
    {
        // Randomly pick a dissonant interval and shift it randomly up/down
        int offset = DissonantOffsets[UnityEngine.Random.Range(0, DissonantOffsets.Length)];
        if (UnityEngine.Random.value < 0.5f)
            offset = -offset;

        int toxicNote = Mathf.Clamp(rootNote + offset, 36, 84);
        int preset = DarkPresets[UnityEngine.Random.Range(0, DarkPresets.Length)];

        return new ToxicSound(toxicNote, preset);
    }
}
