using UnityEngine;
using MidiPlayerTK;

public enum CollectionEffectType
{
    Star,
    NoteSpawner,
    LoopExpansion,
    TrackClear,
    DrumLoopPattern // NEW!
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

    public void PlayEffect(CollectionEffectType type, MusicalRole? role = null)
    {
        int baseNote = 72;
        int preset = defaultFxPreset;

        switch (type)
        {
            case CollectionEffectType.Star:
                baseNote = 90;
                preset = 98; // Crystal
                break;

            case CollectionEffectType.NoteSpawner:
                baseNote = 67;
                preset = 97;          // Soundtrack FX
                break;
            case CollectionEffectType.LoopExpansion:
            case CollectionEffectType.TrackClear:
                var resolvedRole = role ?? MusicalRole.Harmony;
                baseNote = GetNoteForRole(resolvedRole);
                preset = GetPresetForRole(resolvedRole);
                break;
        }

        PlayNoteEffect(baseNote, 100, 240, preset);
    }

    public void PlayNoteEffect(int midiNote, float velocity, int durationTicks = 60, int? overridePreset = null)
    {
        if (midiStreamPlayer == null)
        {
            Debug.LogWarning("CollectionSoundManager: MidiStreamPlayer not assigned!");
            return;
        }

        int durationMs = Mathf.RoundToInt(durationTicks * (60000f / (120f * 480f))); // assuming 120 BPM
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedBank = 0;
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedPreset = overridePreset ?? defaultFxPreset;

        MPTKEvent noteOn = new MPTKEvent()
        {
            Command = MPTKCommand.NoteOn,
            Value = Mathf.Clamp(midiNote, 0, 127),
            Channel = midiChannel,
            Duration = durationMs,
            Velocity = Mathf.Clamp((int)velocity, 30, 127),
        };
    Debug.LogWarning($"Playing {noteOn}");
        midiStreamPlayer.MPTK_PlayEvent(noteOn);

        
        
    }

    private int GetNoteForRole(MusicalRole role)
    {
        return role switch
        {
            MusicalRole.Bass => 60,
            MusicalRole.Harmony => 72,
            MusicalRole.Lead => 84,
            MusicalRole.Groove => 67,
            _ => 72
        };
    }

    private int GetPresetForRole(MusicalRole role)
    {
        return role switch
        {
            MusicalRole.Bass => 38,       // Synth Bass 1
            MusicalRole.Harmony => 52,    // Concert Choir
            MusicalRole.Lead => 80,       // Square Lead
            MusicalRole.Groove => 11, // Vibraphone
            _ => defaultFxPreset
        };
    }
}
