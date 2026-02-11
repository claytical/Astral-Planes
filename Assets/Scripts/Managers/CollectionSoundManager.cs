using System.Collections;
using UnityEngine;

public enum SoundEffectPreset
{
    Aether = 1,
    Bloom = 10,
    Dust = 11,
    Boundary = 13
}

public class CollectionSoundManager : MonoBehaviour
{
    [Header("FX Voice (MidiVoice)")]
    [SerializeField] private MidiVoice fxVoice;

    [Header("FX Program Defaults")]
    [Tooltip("FX voice program preset (used for PlayEffect unless overridden).")]
    [SerializeField] private SoundEffectPreset defaultFxPreset = SoundEffectPreset.Dust;

    // All presets assumed to be in Bank 0
    private const int fxBank = 0;

    public static CollectionSoundManager Instance;

    private static readonly int[] CMajorNotes =
    {
        60, 62, 64, 65, 67, 69, 71, 72
    };

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        if (!fxVoice) fxVoice = GetComponent<MidiVoice>();

        BindFxVoiceAuthorityIfNeeded();
        StartCoroutine(WaitForSoundFontReady());
    }

    private IEnumerator WaitForSoundFontReady()
    {
        // We can’t see fxVoice’s MidiStreamPlayer directly unless you expose it.
        // So we gate on the global soundfont readiness only.
        while (!MidiPlayerTK.MidiPlayerGlobal.MPTK_SoundFontIsReady)
            yield return null;

        if (fxVoice == null)
        {
            Debug.LogWarning("[CollectionSoundManager] fxVoice (MidiVoice) missing; FX disabled.");
            yield break;
        }

        // Set default program for FX
        fxVoice.PlayOneShotMs127(60, 1, 1, (int)defaultFxPreset, fxBank); // "poke" to force channel init
       // fxVoice.SetProgram((int)defaultFxPreset, fxBank);

        Debug.Log("✅ SoundFont ready. CollectionSoundManager FX voice initialized.");
    }

    // ============================================================
    // Public API
    // ============================================================

    public void PlayEffect(SoundEffectPreset preset)
    {
        if (fxVoice == null) return;

        int note = CMajorNotes[Random.Range(0, CMajorNotes.Length)];
        fxVoice.PlayOneShotMs127(note, durationMs: 400, velocity127: 100, overridePreset: (int)preset, overrideBank: fxBank);
    }

    public void PlayPhaseStarImpact(InstrumentTrack track, NoteSet previewSet, float forceVel01 = 0.75f)
    {
        if (fxVoice == null || track == null) return;

        int steps = Mathf.Max(1, track.GetTotalSteps());
        int note = previewSet != null
            ? previewSet.GetNoteForPhaseAndRole(track, Random.Range(0, steps))
            : Mathf.Clamp(track.lowestAllowedNote, track.lowestAllowedNote, track.highestAllowedNote);

        int vel = Mathf.RoundToInt(Mathf.Lerp(70, 120, Mathf.Clamp01(forceVel01)));

        // Use the track's instrument program for this impact (consistent timbre with role)
        fxVoice.PlayOneShotMs127(note, durationMs: 180, velocity127: vel, overridePreset: track.Preset, overrideBank: track.Bank);
    }

    /// <summary>
    /// “Announce” a delayed burst with a quick swell/arpeggio before the next downbeat.
    /// Schedules via DSP time for tightness.
    /// </summary>
    public void PlayBurstLeadIn(InstrumentTrack track, NoteSet set, float secondsUntilDownbeat)
    {
        if (fxVoice == null || track == null || secondsUntilDownbeat <= 0f) return;
        StartCoroutine(BurstLeadInRoutine(track, set, secondsUntilDownbeat));
    }

    public void PlayNoteSpawnerSound(InstrumentTrack track, NoteSet noteSet)
    {
        PlayNoteSpawnerSound(track, noteSet, 0);
    }
    private void BindFxVoiceAuthorityIfNeeded()
    {
        if (fxVoice == null) return;

        // Ensure MidiStreamPlayer is wired (common failure when fxVoice is a different object than the player).
        // If your fxVoice is on the same GO as MidiStreamPlayer, this resolves correctly.
        var player = fxVoice.GetComponent<MidiPlayerTK.MidiStreamPlayer>() ?? fxVoice.GetComponentInParent<MidiPlayerTK.MidiStreamPlayer>();
        if (player != null)
            fxVoice.SetMidiStreamPlayer(player); // add this setter to MidiVoice if you don't already have it

        // Ensure timing authority is wired (THIS is what was missing for Solo/FX).
        var gfm = GameFlowManager.Instance;
        if (gfm != null && gfm.activeDrumTrack != null)
            fxVoice.SetDrumTrack(gfm.activeDrumTrack);
    }

    public void PlayNoteSpawnerSound(InstrumentTrack track, NoteSet noteSet, int step)
    {
        if (fxVoice == null || track == null || noteSet == null) return;

        int s = Mathf.Clamp(step, 0, 15);
        int note = noteSet.GetNoteForPhaseAndRole(track, s);

        int vel = Mathf.RoundToInt(Random.Range(60f, 100f));
        int durMs = 240;

        fxVoice.PlayOneShotMs127(note, durMs, vel, overridePreset: track.Preset, overrideBank: track.Bank);
    }

    public void PlayDustInteraction(InstrumentTrack track, NoteSet ambientContext, float force01, DustBehaviorType behavior)
    {
        if (fxVoice == null || track == null) return;

        if (behavior == DustBehaviorType.PushThrough)
        {
            PlayDustPushThroughResolve(track, ambientContext, force01);
            return;
        }

        int note = PickContextualNote(track, ambientContext, behavior);
        int vel = Mathf.RoundToInt(Mathf.Lerp(50, 115, Mathf.Clamp01(force01)));
        int durMs = (behavior == DustBehaviorType.Stop) ? 120 : 80;

        fxVoice.PlayOneShotMs127(note, durMs, vel, overridePreset: track.Preset, overrideBank: track.Bank);
    }

    // ============================================================
    // Dust “push through” resolve (your existing design)
    // ============================================================

    private void PlayDustPushThroughResolve(InstrumentTrack track, NoteSet ambientContext, float force01)
    {
        int baseNote = PickContextualNote(track, ambientContext, DustBehaviorType.PushThrough);

        int root = FitToMidiBand(baseNote, 45, 57);
        int grit = Mathf.Clamp(root + 1, 0, 127);
        int fifth = Mathf.Clamp(root + 7, 0, 127);
        int octave = Mathf.Clamp(root + 12, 0, 127);

        float t = Mathf.Clamp01(force01);

        int velMain = Mathf.RoundToInt(Mathf.Lerp(85, 127, t));
        int velGrit = Mathf.RoundToInt(Mathf.Lerp(45, 80, t));
        int durGrit = Mathf.RoundToInt(Mathf.Lerp(35, 60, t));
        int durChord = Mathf.RoundToInt(Mathf.Lerp(110, 170, t));

        // friction
        fxVoice.PlayOneShotMs127(grit, durGrit, velGrit, overridePreset: track.Preset, overrideBank: track.Bank);

        // resolution chord slightly delayed
        StartCoroutine(PlayDelayedChord(track, root, fifth, octave, velMain, durChord, 0.05f));
    }

    private IEnumerator PlayDelayedChord(InstrumentTrack track, int root, int fifth, int octave, int vel, int durMs, float delaySeconds)
    {
        if (delaySeconds > 0f) yield return new WaitForSeconds(delaySeconds);

        fxVoice.PlayOneShotMs127(root, durMs, vel, overridePreset: track.Preset, overrideBank: track.Bank);
        fxVoice.PlayOneShotMs127(fifth, durMs, vel, overridePreset: track.Preset, overrideBank: track.Bank);

        int vOct = Mathf.Clamp(Mathf.RoundToInt(vel * 0.9f), 1, 127);
        fxVoice.PlayOneShotMs127(octave, durMs, vOct, overridePreset: track.Preset, overrideBank: track.Bank);
    }

    // ============================================================
    // Burst lead-in (DSP scheduling)
    // ============================================================

    private IEnumerator BurstLeadInRoutine(InstrumentTrack track, NoteSet set, float remain)
    {
        int taps = Mathf.Clamp(Mathf.FloorToInt(remain / 0.09f), 2, 4);
        double t0 = AudioSettings.dspTime;
        double dt = remain / (taps + 0.75); // land slightly early
        int steps = Mathf.Max(1, track.GetTotalSteps());

        for (int i = 0; i < taps; i++)
        {
            int step = (i * steps) / Mathf.Max(1, taps);
            int note = (set != null) ? set.GetNoteForPhaseAndRole(track, step) : track.lowestAllowedNote;
            int vel = Mathf.RoundToInt(Mathf.Lerp(80, 120, (i + 1f) / taps));
            double when = t0 + dt * i;

            // scheduled, uses track timbre
            // We schedule by temporarily overriding program per event.
            // Easiest: schedule using fxVoice’s current program and do a one-shot override “poke”.
            // If you want true per-event override scheduling, add ScheduleOneShotMs127(...) to MidiVoice.
//            fxVoice.SetProgram(track.Preset, track.Bank);
            fxVoice.ScheduleNoteMs127(note, durationMs: 100, velocity127: vel, whenDSP: when);
        }

        yield return null;
    }
    private bool TryBindFxVoice()
    {
        if (fxVoice == null)
            fxVoice = GetComponent<MidiVoice>();

        if (fxVoice == null)
            return false;

        // Ensure the voice has a MidiStreamPlayer reference.
        var player = fxVoice.GetComponent<MidiPlayerTK.MidiStreamPlayer>()
                     ?? fxVoice.GetComponentInParent<MidiPlayerTK.MidiStreamPlayer>();
        if (player != null)
            fxVoice.SetMidiStreamPlayer(player);

        // Ensure timing authority (DrumTrack) is injected.
        var gfm = GameFlowManager.Instance;
        if (gfm != null && gfm.activeDrumTrack != null)
            fxVoice.SetDrumTrack(gfm.activeDrumTrack);

        return true;
    }

    // ============================================================
    // Note selection helpers
    // ============================================================

    private int PickContextualNote(InstrumentTrack track, NoteSet set, DustBehaviorType b)
    {
        if (track == null) return 60;
        if (set == null) return Mathf.Clamp(track.lowestAllowedNote, track.lowestAllowedNote, track.highestAllowedNote);

        switch (b)
        {
            case DustBehaviorType.Repel:
            {
                var hi = set.GetNoteList();
                if (hi != null && hi.Count > 1) return hi[Mathf.Max(0, hi.Count - 2)];
                break;
            }
            case DustBehaviorType.Stop:
            {
                var pool = set.GetNoteList();
                if (pool != null && pool.Count > 0) return pool[0]; // root-ish
                break;
            }
            default: // PushThrough
                return set.GetNextArpeggiatedNote(Random.Range(0, Mathf.Max(1, track.GetTotalSteps())));
        }

        return Mathf.Clamp(track.lowestAllowedNote, track.lowestAllowedNote, track.highestAllowedNote);
    }

    private int FitToMidiBand(int note, int minInclusive, int maxInclusive)
    {
        int n = note;
        while (n < minInclusive) n += 12;
        while (n > maxInclusive) n -= 12;
        return Mathf.Clamp(n, 0, 127);
    }
}
