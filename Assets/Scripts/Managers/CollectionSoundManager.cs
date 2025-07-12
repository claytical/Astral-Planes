using System.Collections;
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

public enum SoundEffectMood {
    Friendly = 10,
    Toxic = 9
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

        // Now safe to assign
        midiStreamPlayer.MPTK_ChannelPresetChange(midiChannel, 10); // Use this for safe patch change

        // Optionally reinforce fallback
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedPreset = 10;
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedBank = fxBank;
    }


    public void PlayEffect(int mood)
    {
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedPreset = 10;
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
   

}