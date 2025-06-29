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
    private const int fxBank = 0;

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
        int root = 60; // Middle C
        int note = root; // Default

        switch (type)
        {
            case CollectionEffectType.PhaseStar:
                note = root + 24; // C6 – bright
                break;
            case CollectionEffectType.Aether:
                note = root + 12 + Random.Range(-2, 3); // ~C5 + variation
                break;
            case CollectionEffectType.Burst:
                note = root + Random.Range(-6, 6); // center punch
                break;
            case CollectionEffectType.Core:
                note = root - 12; // C3 – low pulse
                break;
            case CollectionEffectType.MazeFriendly:
                note = FriendlyNoteUtility.GetNote(root);
                break;
            case CollectionEffectType.MazeToxic:
                note = ToxicNoteUtility.GetNote(root);
                break;
        }

        // Always use custom Preset 8 (percussive)
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedBank = fxBank;
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedPreset = 8;

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
    private static readonly int[] MajorScaleOffsets = { 0, 2, 4, 5, 7, 9, 11 };

    public static int GetNote(int root = 60)
    {
        int offset = MajorScaleOffsets[UnityEngine.Random.Range(0, MajorScaleOffsets.Length)];
        return Mathf.Clamp(root + offset, 48, 84);
    }
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
    private static readonly int[] DissonantOffsets = { 1, 3, 6, 8, 10, 13, 14 };

    public static int GetNote(int root = 60)
    {
        int offset = DissonantOffsets[UnityEngine.Random.Range(0, DissonantOffsets.Length)];
        if (UnityEngine.Random.value < 0.5f) offset = -offset;
        return Mathf.Clamp(root + offset, 36, 84);
    }
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
