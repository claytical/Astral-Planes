using System.Collections;
using System.Reflection;
using MidiPlayerTK;
using UnityEngine;

/// <summary>
/// Pure "voice" component: plays MIDI notes (loop voice, confirms, special FX).
/// Owns nothing about loop state; it only emits sound.
/// </summary>
[DisallowMultipleComponent]
public class MidiVoice : MonoBehaviour
{
    [Header("Refs (optional if bound by InstrumentTrack at runtime)")]
    [SerializeField] private MidiStreamPlayer midiStreamPlayer;
    [SerializeField] private DrumTrack drumTrack;

    [Header("Channel / Program")]
    [SerializeField] private int channel = 0;
    [SerializeField] private int preset = 0;
    [SerializeField] private int bank = 0;
        
    private static PropertyInfo _piForcedPreset;
    private static PropertyInfo _piForcedBank;
    private static bool _forcedProgramPropsCached;

    // Optional callback so trimming logic can use track’s windowing model.
    private System.Func<float> _remainingActiveWindowSec;
// Add near your existing private fields:
    private bool _warnedMissingDrums;
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    private void AssertNotOnFxChannel(string context)
    {
        // Change these to match your project conventions.
        const int FxReservedChannel = 5;

        // If this MidiVoice is *supposed* to be FX, you can add a bool flag instead.
        bool thisIsFx = gameObject.name.IndexOf("fx", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("fx", System.StringComparison.OrdinalIgnoreCase) >= 0;

        if (!thisIsFx && channel == FxReservedChannel)
        {
            Debug.LogError($"[MIDI] Non-FX voice is using FX channel {FxReservedChannel}. context={context} voice={name}", this);
        }
    }
    private void PushProgram(int _preset, int bank, out int prevPreset, out int prevBank)
    {
        prevPreset = -1;
        prevBank = -1;

        if (midiStreamPlayer == null) return;
        var arr = midiStreamPlayer.MPTK_Channels;
        if (arr == null) return;
        if (channel < 0 || channel >= arr.Length) return;

        // If MPTK exposes getters, use them; if not, you can just “not restore”
        // and rely on strict channel isolation (recommended).
        // prevPreset = arr[channel].ForcedPreset;
        // prevBank   = arr[channel].ForcedBank;

        arr[channel].ForcedPreset = _preset;
        arr[channel].ForcedBank   = bank;
    }

    private void PopProgram(int prevPreset, int prevBank)
    {
        if (prevPreset < 0 || prevBank < 0) return;

        if (midiStreamPlayer == null) return;
        var arr = midiStreamPlayer.MPTK_Channels;
        if (arr == null) return;
        if (channel < 0 || channel >= arr.Length) return;

        arr[channel].ForcedPreset = prevPreset;
        arr[channel].ForcedBank   = prevBank;
    }

    /// <summary>
    /// Authority binding (e.g., GameFlowManager) can call this to ensure the voice can
    /// convert ticks using the active DrumTrack.
    /// </summary>
    public void SetDrumTrack(DrumTrack drums)
    {
        if (drums != null) drumTrack = drums;
    }
    
    public void Bind(
        MidiStreamPlayer player,
        DrumTrack drums,
        System.Func<float> remainingActiveWindowSec)
    {
        midiStreamPlayer = player;
        drumTrack = drums;
        _remainingActiveWindowSec = remainingActiveWindowSec;
    }
    private static void CacheForcedProgramPropsIfNeeded(object channelObj)
    {
        if (_forcedProgramPropsCached) return;
        _forcedProgramPropsCached = true;

        if (channelObj == null) return;

        var t = channelObj.GetType();
        // Look for public instance properties ForcedPreset / ForcedBank
        _piForcedPreset = t.GetProperty("ForcedPreset", BindingFlags.Instance | BindingFlags.Public);
        _piForcedBank   = t.GetProperty("ForcedBank",   BindingFlags.Instance | BindingFlags.Public);
    }
    private bool TryGetMptkChannelObject(out object channelObj)
    {
        channelObj = null;

        if (midiStreamPlayer == null)
            return false;

        var arr = midiStreamPlayer.MPTK_Channels; // this compiles in your project
        if (arr == null)
            return false;

        if (channel < 0 || channel >= arr.Length)
            return false;

        channelObj = arr[channel];
        return channelObj != null;
    }
    private bool TrySetForcedProgram(object channelObj, int forcedPreset, int forcedBank)
    {
        if (channelObj == null) return false;

        CacheForcedProgramPropsIfNeeded(channelObj);

        bool ok = false;

        try
        {
            if (_piForcedPreset != null)
            {
                _piForcedPreset.SetValue(channelObj, forcedPreset, null);
                ok = true;
            }
            if (_piForcedBank != null)
            {
                _piForcedBank.SetValue(channelObj, forcedBank, null);
                ok = true;
            }
        }
        catch
        {
            // Swallow: property may exist but setter may throw in some versions/configs.
            return false;
        }

        return ok;
    }

    /// <summary>
    /// Plays a normal note. Expects duration in TICKS (MPTK convention),
    /// converts to ms using drum BPM, and trims to the remaining audible window.
    /// </summary>
    public void PlayNoteTicks(int note, int durationTicks, float velocity127)
    {
        if (midiStreamPlayer == null || drumTrack == null || drumTrack.drumLoopBPM <= 0f)
        {
            if (!_warnedMissingDrums)
            {
                Debug.LogError("[MidiVoice] Missing MidiStreamPlayer or DrumTrack/BPM; cannot play note.");
                _warnedMissingDrums = true;
            }
            return;
        }
        
        // Convert ticks → ms (480 ticks per quarter note)
        int durationMs = Mathf.RoundToInt(durationTicks * (60000f / (drumTrack.drumLoopBPM * 480f)));

        // Trim to remaining audible window (if provided)
        if (_remainingActiveWindowSec != null)
        {
            float remainSec = _remainingActiveWindowSec();
            if (!float.IsPositiveInfinity(remainSec) && remainSec < float.MaxValue)
            {
                int maxMs = Mathf.Max(10, Mathf.FloorToInt(remainSec * 1000f));
                durationMs = Mathf.Min(durationMs, maxMs);
            }
        }

        midiStreamPlayer.MPTK_Channels[channel].ForcedPreset = preset;
        midiStreamPlayer.MPTK_Channels[channel].ForcedBank = bank;

        var noteOn = new MPTKEvent
        {
            Command = MPTKCommand.NoteOn,
            Value = note,
            Channel = channel,
            Duration = durationMs,
            Velocity = Mathf.Clamp((int)velocity127, 1, 127),
        };

        midiStreamPlayer.MPTK_PlayEvent(noteOn);
    }
    public void SetMidiStreamPlayer(MidiPlayerTK.MidiStreamPlayer player)
    {
        if (player != null) midiStreamPlayer = player;
    }

    /// <summary>Change program for this voice (forced preset/bank on its channel).</summary>
    public void SetProgram(int preset, int bank)
    {
        if (midiStreamPlayer == null) return;
        var arr = midiStreamPlayer.MPTK_Channels;
        if (arr == null) return;
        if (channel < 0 || channel >= arr.Length) return;

        // The only invariant you need:
        arr[channel].ForcedPreset = preset;
        arr[channel].ForcedBank   = bank;
    }


    public void ScheduleNoteMs127(int note, int durationMs, int velocity127, double whenDSP)
    {
        if (midiStreamPlayer == null)
        {
            Debug.LogWarning("[MidiVoice] MIDI player is null; cannot schedule note.");
            return;
        }

        midiStreamPlayer.MPTK_Channels[channel].ForcedPreset = preset;
        midiStreamPlayer.MPTK_Channels[channel].ForcedBank   = bank;

        double now = AudioSettings.dspTime;
        double sec = Mathf.Max(0f, (float)(whenDSP - now));
        long delayMs = (long)Mathf.RoundToInt((float)(sec * 1000.0));

        var ev = new MPTKEvent
        {
            Command  = MPTKCommand.NoteOn,
            Value    = Mathf.Clamp(note, 0, 127),
            Channel  = channel,
            Duration = Mathf.Max(1, durationMs),
            Velocity = Mathf.Clamp(velocity127, 1, 127),
            Delay    = delayMs
        };

        midiStreamPlayer.MPTK_PlayEvent(ev);
    }

    /// <summary>
    /// Temporary override program for one-shot FX without permanently changing the voice.
    /// </summary>
    public void PlayOneShotMs127(int note, int durationMs, int velocity127, int overridePreset, int overrideBank)
    {
        if (midiStreamPlayer == null)
            return;

        if (!TryGetMptkChannelObject(out var chObj))
            return;

        // Best-effort override program
        TrySetForcedProgram(chObj, overridePreset, overrideBank);

        var ev = new MPTKEvent
        {
            Command  = MPTKCommand.NoteOn,
            Value    = Mathf.Clamp(note, 0, 127),
            Channel  = channel,
            Duration = Mathf.Max(1, durationMs),
            Velocity = Mathf.Clamp(velocity127, 1, 127),
        };

        midiStreamPlayer.MPTK_PlayEvent(ev);

        // Best-effort restore
        TrySetForcedProgram(chObj, preset, bank);
    }
    /// <summary>
    /// Short tactile confirmation. Duration is fixed and NOT trimmed.
    /// Velocity input expects 0..1.
    /// </summary>
    public void PlayCollectionConfirm(int note, float velocity01)
    {
        if (midiStreamPlayer == null) return;

        const int confirmDurationMs = 35;

        midiStreamPlayer.MPTK_Channels[channel].ForcedPreset = preset;
        midiStreamPlayer.MPTK_Channels[channel].ForcedBank = bank;

        int v = Mathf.Clamp(Mathf.RoundToInt(velocity01 * 0.45f), 1, 80);

        var ev = new MPTKEvent
        {
            Command = MPTKCommand.NoteOn,
            Value = note,
            Channel = channel,
            Duration = confirmDurationMs,
            Velocity = v,
        };

        midiStreamPlayer.MPTK_PlayEvent(ev);
    }

    /// <summary>
    /// "Dark" note variant with pitch bend dip then reset.
    /// Velocity input expects 0..127-ish; you can pass 1..127.
    /// </summary>
    public void PlayDarkNote(int note, int durationMs, float velocity127, float resetDelaySec = 0.2f)
    {
        if (midiStreamPlayer == null)
        {
            Debug.LogWarning("[MidiVoice] Cannot play dark note: MIDI player is null.");
            return;
        }

        // Pitch bend downward (quarter-ish tone feel; tune as desired)
        int bendValue = 4096; // halfway down from center (8192)
        midiStreamPlayer.MPTK_Channels[channel].fluid_channel_pitch_bend(bendValue);

        var darkNote = new MPTKEvent
        {
            Command = MPTKCommand.NoteOn,
            Value = note,
            Channel = channel,
            Duration = durationMs,
            Velocity = Mathf.Clamp((int)velocity127, 0, 127),
        };

        midiStreamPlayer.MPTK_PlayEvent(darkNote);

        if (resetDelaySec > 0f)
            StartCoroutine(ResetPitchBendAfterDelay(resetDelaySec));
    }

    private IEnumerator ResetPitchBendAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        if (midiStreamPlayer != null)
            midiStreamPlayer.MPTK_Channels[channel].fluid_channel_pitch_bend(8192); // center
    }
}
