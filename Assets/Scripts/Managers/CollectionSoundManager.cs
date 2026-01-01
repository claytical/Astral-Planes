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

// --- tiny utilities (already likely present; adjust to your class style) ---
private int PickContextualNote(NoteSet ctx, InstrumentTrack track, DustBehaviorType b)
{
    if (ctx == null) return track.lowestAllowedNote;
    switch (b)
    {
        case DustBehaviorType.Repel: // bright, a touch tense
            var sorted = ctx.GetSortedNoteList();
            return sorted[Mathf.Min(sorted.Count - 1, sorted.Count - 2)];
        case DustBehaviorType.Stop: // root/third to feel “grounded”
            return ctx.GetRootNote();
        default: // PushThrough: mid-register arpeggio note
            return ctx.GetNextArpeggiatedNote(UnityEngine.Random.Range(0, track.GetTotalSteps()));
    }
}


    // Controller-routed dust interaction (vehicles do NOT own tracks)
    public void PlayDustInteraction(InstrumentTrackController controller, float force01, DustBehaviorType behavior)
    {
        if (controller == null || midiStreamPlayer == null) return;
        var track = controller.GetAmbientContextTrack();
        var set   = controller.GetGlobalContextNoteSet();
        if (track == null) return;
        PlayDustInteraction(track, set, force01, behavior);
    }

    // Track+NoteSet-routed dust sound
    public void PlayDustInteraction(InstrumentTrack track, NoteSet ambientContext, float force01, DustBehaviorType behavior)
    {
        if (track == null || midiStreamPlayer == null) return;
        int note = PickContextualNote(track, ambientContext, behavior);
        int vel  = Mathf.RoundToInt(Mathf.Lerp(50, 115, Mathf.Clamp01(force01)));
        int dur  = behavior == DustBehaviorType.Stop ? 120 : 80;
        SendNote(track, note, vel, dur);
    }

    // Star impact preview (use upcoming NoteSet if available)
    public void PlayPhaseStarImpact(InstrumentTrack track, NoteSet previewSet, float forceVel01 = .75f)
    {
        if (track == null || midiStreamPlayer == null) return;
        int steps = Mathf.Max(1, track.GetTotalSteps());
        int note  = previewSet != null
            ? previewSet.GetNoteForPhaseAndRole(track, UnityEngine.Random.Range(0, steps))
            : Mathf.Clamp(track.lowestAllowedNote, track.lowestAllowedNote, track.highestAllowedNote);
        int vel = Mathf.RoundToInt(Mathf.Lerp(70, 120, Mathf.Clamp01(forceVel01)));
        SendNote(track, note, vel, 180);
    }

    // “Announce” a delayed burst with a quick swell/arpeggio before the next downbeat
    public void PlayBurstLeadIn(InstrumentTrack track, NoteSet set, float secondsUntilDownbeat)
    {
        if (track == null || midiStreamPlayer == null || secondsUntilDownbeat <= 0f) return;
        StartCoroutine(BurstLeadInRoutine(track, set, secondsUntilDownbeat));
    }

    private IEnumerator BurstLeadInRoutine(InstrumentTrack track, NoteSet set, float remain)
    {
        int taps = Mathf.Clamp(Mathf.FloorToInt(remain / 0.09f), 2, 4);
        double t0 = AudioSettings.dspTime;
        double dt = remain / (taps + 0.75); // land slightly early
        int steps = Mathf.Max(1, track.GetTotalSteps());

        for (int i = 0; i < taps; i++)
        {
            int step = (i * steps) / Mathf.Max(1, taps);
            int note = set != null ? set.GetNoteForPhaseAndRole(track, step) : track.lowestAllowedNote;
            int vel  = Mathf.RoundToInt(Mathf.Lerp(80, 120, (i + 1f) / taps));
            ScheduleNoteDSP(track, note, vel, 100, t0 + dt * i);
        }
        yield return null;
    }

    // --- small utilities ---
    private int PickContextualNote(InstrumentTrack track, NoteSet set, DustBehaviorType b)
    {
        if (set == null) return Mathf.Clamp(track.lowestAllowedNote, track.lowestAllowedNote, track.highestAllowedNote);
        switch (b)
        {
            case DustBehaviorType.Repel:
                var hi = set.GetNoteList(); if (hi != null && hi.Count > 0) return hi[Mathf.Max(0, hi.Count - 2)];
                break;
            case DustBehaviorType.Stop:
                if (set.GetNoteList() is List<int> pool && pool.Count > 0) return pool[0]; // root-ish
                break;
            default: // PushThrough
                return set.GetNextArpeggiatedNote(UnityEngine.Random.Range(0, Mathf.Max(1, track.GetTotalSteps())));
        }
        return Mathf.Clamp(track.lowestAllowedNote, track.lowestAllowedNote, track.highestAllowedNote);
    }

    private void SendNote(InstrumentTrack track, int midi, int vel, int durMs)
    {
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedPreset = track.preset;
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedBank   = track.bank;
        midiStreamPlayer.MPTK_PlayEvent(new MPTKEvent {
            Command = MPTKCommand.NoteOn, Channel = midiChannel,
            Value = midi, Velocity = vel, Duration = durMs
        });
    }

    private void ScheduleNoteDSP(InstrumentTrack track, int midi, int vel, int durMs, double whenDSP)
    {
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedPreset = track.preset;
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedBank   = track.bank;
        midiStreamPlayer.MPTK_PlayEvent(new MPTKEvent {
            Command = MPTKCommand.NoteOn, Channel = midiChannel,
            Value = midi, Velocity = vel, Duration = durMs,
            Delay = (long)(whenDSP - AudioSettings.dspTime)
        });
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
// CollectionSoundManager.cs

    public void PlayNoteSpawnerSound(InstrumentTrack track, NoteSet noteSet)
    {
        // Backward compatible fallback: use step 0 if caller doesn't know.
        PlayNoteSpawnerSound(track, noteSet, 0);
    }

    public void PlayNoteSpawnerSound(InstrumentTrack track, NoteSet noteSet, int step)
    {
        if (track == null || noteSet == null || midiStreamPlayer == null) return;

        // Clamp into a sane step range. If you later expand to variable steps, pass the right modulus in.
        int s = Mathf.Clamp(step, 0, 15);

        int note = noteSet.GetNoteForPhaseAndRole(track, s);
        float velocity = Random.Range(60f, 100f);
        int duration = 240;

        int preset = track.preset;
        midiStreamPlayer.MPTK_Channels[midiChannel].ForcedPreset = preset;

        var noteEvent = new MPTKEvent()
        {
            Command  = MPTKCommand.NoteOn,
            Value    = note,
            Channel  = midiChannel,
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