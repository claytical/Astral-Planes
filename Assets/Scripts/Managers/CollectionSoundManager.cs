using System.Collections;
using System.Collections.Generic;
using System.Linq;
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

public enum SoundEffectMood {
    Friendly = 10,
    Toxic = 9
}

public enum SoundEffectPreset
{
    Aether = 1,
    Bloom = 10,
    Dust = 11,
    Boundary = 13
}

public class CollectionSoundManager : MonoBehaviour
{
    [Header("MIDI Settings")]
    public MidiStreamPlayer midiStreamPlayer;
    public int midiChannel = 5;
    // All presets assumed to be in Bank 0
    private const int fxBank = 0;

    public static CollectionSoundManager Instance;
    private static readonly int[] cMajorNotes = new int[]
    {
        60, // C4
        62, // D4
        64, // E4
        65, // F4
        67, // G4
        69, // A4
        71, // B4
        72  // C5
    };

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        Debug.Log("🎵 Presets loaded so far: " + MidiPlayerGlobal.MPTK_CountPresetLoaded);

        // Wait until SoundFont is ready
        StartCoroutine(WaitForSoundFontReady());
    }

    private IEnumerator WaitForSoundFontReady()
    {
        // Wait until MPTK_Channels is initialized and soundfont is ready
        while (!MidiPlayerGlobal.MPTK_IsReady() || midiStreamPlayer.MPTK_Channels == null || midiStreamPlayer.MPTK_Channels.Length <= midiChannel)
        {
            yield return null;
        }
        foreach (var presets in MidiPlayerGlobal.MPTK_ListPreset)
        {
            Debug.Log($"🎹 Preset #{presets.Label}");
        }


        Debug.Log("✅ SoundFont ready. Assigning preset and bank.");

        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedPreset = (int)SoundEffectPreset.Dust;
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedBank = fxBank;
    }


    public void PlayEffect(SoundEffectPreset preset)
    {
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedPreset = (int)preset;
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedBank = 0;
        int[] cMajorNotes = { 60, 62, 64, 65, 67, 69, 71, 72 };
        int note = cMajorNotes[UnityEngine.Random.Range(0, cMajorNotes.Length)];

        MPTKEvent noteOn = new MPTKEvent()
        {
            Command = MPTKCommand.NoteOn,
            Value = note,
            Channel = midiChannel,
            Duration = 400, // ✅ Fixed duration scaling
            Velocity = 100,
        };
        midiStreamPlayer.MPTK_PlayEvent(noteOn);
    }
    public void PlayNoteSpawnerSound(InstrumentTrack track, NoteSet noteSet)
    {
        if (track == null || noteSet == null || midiStreamPlayer == null) return;

        int note = noteSet.GetNoteForPhaseAndRole(track, Random.Range(0, 16));
        float velocity = Random.Range(60f, 100f);
        int duration = 240;

        int preset = track.preset; // 🎹 Use the track's instrument
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedPreset = preset;

        var noteEvent = new MPTKEvent()
        {
            Command = MPTKCommand.NoteOn,
            Value = note,
            Channel = midiChannel,
            Duration = duration,
            Velocity = Mathf.RoundToInt(velocity)
        };

        midiStreamPlayer.MPTK_PlayEvent(noteEvent);
    }
    public void PlayMazeChime(MusicalPhase phase, int midiPresetId)
    {
        if (midiStreamPlayer == null) return;

        int rootNote = 60 + ((int)phase % 7); // C4 + offset
        ScaleType scale = (ScaleType)((int)phase % System.Enum.GetValues(typeof(ScaleType)).Length);

        // Manually simulate allowed note range
        int lowest = 48;
        int highest = 84;

        // Build note list manually (subset of NoteSet logic)
        int[] pattern = ScalePatterns.Patterns[scale];
        List<int> notes = new();
        for (int pitch = lowest; pitch <= highest; pitch++)
        {
            int semitoneAboveRoot = (pitch - rootNote) % 12;
            if (semitoneAboveRoot < 0) semitoneAboveRoot += 12;

            if (pattern.Contains(semitoneAboveRoot))
            {
                notes.Add(pitch);
            }
        }

        if (notes.Count == 0)
        {
            Debug.LogWarning("⚠️ No notes available for maze chime.");
            return;
        }

        int note = notes[Random.Range(0, notes.Count)];
        int velocity = Random.Range(60, 100);
        int duration = 200 + Random.Range(-40, 40);

        midiStreamPlayer.MPTK_ChannelPresetChange(midiChannel, midiPresetId);
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedPreset = midiPresetId;

        var noteEvent = new MPTKEvent()
        {
            Command = MPTKCommand.NoteOn,
            Value = note,
            Channel = midiChannel,
            Duration = duration,
            Velocity = velocity
        };

        midiStreamPlayer.MPTK_PlayEvent(noteEvent);
    }

}