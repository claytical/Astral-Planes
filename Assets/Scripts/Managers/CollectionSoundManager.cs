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

    public void PlayDustInteraction(InstrumentTrack track, NoteSet ambientContext, float force01, DustBehaviorType behavior)
    {
        if (track == null || midiStreamPlayer == null) return;

        // Boosting branch is already encoded by behavior at call site.
        if (behavior == DustBehaviorType.PushThrough)
        {
            PlayDustPushThroughResolve(track, ambientContext, force01);
            return;
        }

        // Default = treacherous / nervous / "you shouldn't be here"
        int note = PickContextualNote(track, ambientContext, behavior);
        int vel  = Mathf.RoundToInt(Mathf.Lerp(50, 115, Mathf.Clamp01(force01)));
        int dur  = behavior == DustBehaviorType.Stop ? 120 : 80;

        // Existing behavior (or your current dissonant dyad version)
        SendNote(track, note, vel, dur);
    }
private void PlayDustPushThroughResolve(InstrumentTrack track, NoteSet ambientContext, float force01)
{
    // Anchor in the current musical context.
    int baseNote = PickContextualNote(track, ambientContext, DustBehaviorType.PushThrough);

    // Put the anchor into a lower heroic band (weight), but not sub-bass.
    // This is purely register shaping; it does not assume NoteSet APIs.
    int root = FitToMidiBand(baseNote, 45, 57); // A2..A3-ish region

    // "Danger" grace: a very short dissonance that resolves.
    // Minor 2nd above root is the clearest "friction" that resolves cleanly.
    int grit = root + 1;

    // Hero sonority: power chord (+ optional octave).
    int fifth  = root + 7;
    int octave = root + 12;

    // Dynamics: triumphant = louder + slightly longer than the “nervous” hit.
    float t = Mathf.Clamp01(force01);

    int velMain  = Mathf.RoundToInt(Mathf.Lerp(85, 127, t));
    int velGrit  = Mathf.RoundToInt(Mathf.Lerp(45, 80,  t));   // audible but subordinate
    int durGrit  = Mathf.RoundToInt(Mathf.Lerp(35, 60,  t));   // very short
    int durChord = Mathf.RoundToInt(Mathf.Lerp(110, 170, t));  // punch + sustain

    root   = Mathf.Clamp(root,   0, 127);
    grit   = Mathf.Clamp(grit,   0, 127);
    fifth  = Mathf.Clamp(fifth,  0, 127);
    octave = Mathf.Clamp(octave, 0, 127);

    // 1) tiny “friction”
    SendNote(track, grit, velGrit, durGrit);

    // 2) resolution into courage/power, slightly delayed so the ear perceives “turning danger into harmony”
    StartCoroutine(PlayDelayedChord(track, root, fifth, octave, velMain, durChord, 0.05f));
}

private IEnumerator PlayDelayedChord(InstrumentTrack track, int root, int fifth, int octave, int vel, int dur, float delaySeconds)
{
    if (delaySeconds > 0f) yield return new WaitForSeconds(delaySeconds);

    // Wide spacing reads as “heroic” (weight + clarity)
    SendNote(track, root,  vel, dur);
    SendNote(track, fifth, vel, dur);

    // Optional octave: keep it, unless you find it too thick on certain instruments.
    SendNote(track, octave, Mathf.Clamp(Mathf.RoundToInt(vel * 0.9f), 1, 127), dur);
}

private int FitToMidiBand(int note, int minInclusive, int maxInclusive)
{
    // Transpose by octaves to land in a preferred register band.
    int n = note;
    while (n < minInclusive) n += 12;
    while (n > maxInclusive) n -= 12;
    return n;
}

private int MakeDissonantNeighbor(int baseNote, DustBehaviorType behavior, float force01)
{
    // Dissonant intervals in semitones relative to base
    // m2(1), TT(6), m7(10), M7(11), tritone-ish + cluster options.
    // Add/remove to taste.
    int[] intervals = GetDissonantIntervalsForBehavior(behavior);

    // Bias: stronger collisions trend toward harsher intervals (TT/M7),
    // weaker collisions toward m2/m7.
    float t = Mathf.Clamp01(force01);

    // Pick an interval index with a simple force-weighted skew.
    // (No allocations, stable enough, still "alive".)
    int idx;
    if (intervals.Length == 1)
    {
        idx = 0;
    }
    else if (t < 0.33f)
    {
        idx = UnityEngine.Random.Range(0, Mathf.Min(2, intervals.Length));
    }
    else if (t < 0.66f)
    {
        idx = UnityEngine.Random.Range(0, intervals.Length);
    }
    else
    {
        idx = UnityEngine.Random.Range(Mathf.Max(0, intervals.Length - 2), intervals.Length);
    }

    int interval = intervals[idx];

    // Randomly go above or below (prevents constant upward drift)
    int dir = (UnityEngine.Random.value < 0.5f) ? -1 : 1;

    // If we’d fall out of range, flip direction.
    int candidate = baseNote + (dir * interval);
    if (candidate < 0 || candidate > 127)
        candidate = baseNote - (dir * interval);

    return candidate;
}

private int[] GetDissonantIntervalsForBehavior(DustBehaviorType behavior)
{
    // If you want behavior-specific “flavors,” tune here.
    // Keeping defaults conservative: always dissonant, but "Stop" is harsher.
    switch (behavior)
    {
        case DustBehaviorType.Stop:
            // More abrasive when dust stops the player: TT, M7, m2
            return new[] { 6, 11, 1, 10 };

        default:
            // General dust: m2 / m7 / TT / M7
            return new[] { 1, 10, 6, 11 };
    }
}

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