using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using MidiPlayerTK;

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
    private static readonly int[] CMajorNotes = new int[]
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

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        // Wait until SoundFont is ready
        StartCoroutine(WaitForSoundFontReady());
    }
    private IEnumerator WaitForSoundFontReady()
    {
        // Wait until MPTK_Channels is initialized and soundfont is ready
        while (!MidiPlayerGlobal.MPTK_SoundFontIsReady || midiStreamPlayer.MPTK_Channels == null || midiStreamPlayer.MPTK_Channels.Length <= midiChannel)
        {
            yield return null;
        }
        
        Debug.Log("✅ SoundFont ready. Assigning preset and bank.");

        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedPreset = (int)SoundEffectPreset.Dust;
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedBank = fxBank;
    }
}