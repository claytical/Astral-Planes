using System.Collections;
using System.Reflection;
using MidiPlayerTK;
using UnityEngine;
using UnityEngine.Audio;

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
    private int preset;
    [SerializeField] private int bank = 0;
    
    private static bool _forcedProgramPropsCached;

    // Cached per-role volume trim; re-applied on every note-on so it self-heals
    // once MPTK_Channels exists, the same way preset/bank already do.
    private float _volume = 1f;

    // Optional callback so trimming logic can use track’s windowing model.
    private System.Func<float> _remainingActiveWindowSec;

    private bool _warnedMissingDrums;

    public void SetDrumTrack(DrumTrack drums)
    {
        if (drums != null) drumTrack = drums;
    }

    public void SetPreset(int value)
    {
        if (GameFlowManager.VerboseLogging) Debug.Log($"[MidiVoice] Setting preset to {value}");
        preset = value;

        // Prime the channel immediately so the first note doesn't inherit the MPTK default (0),
        // but only if the MIDI player and its channel table are actually ready.
        if (midiStreamPlayer == null)
            return;

        if (midiStreamPlayer.MPTK_Channels == null)
        {
            Debug.LogWarning(
                $"[MidiVoice] MPTK_Channels not ready yet on {name}; " +
                $"preset {preset} cached and will be used on first play/bind.");
            return;
        }

        if (channel < 0 || channel >= midiStreamPlayer.MPTK_Channels.Length)
        {
            Debug.LogWarning(
                $"[MidiVoice] Channel out of range on {name}: channel={channel}, " +
                $"channelsLength={midiStreamPlayer.MPTK_Channels.Length}. " +
                $"Preset {preset} cached.");
            return;
        }

        if (midiStreamPlayer.MPTK_Channels[channel] == null)
        {
            Debug.LogWarning(
                $"[MidiVoice] Channel slot {channel} is null on {name}; " +
                $"preset {preset} cached.");
            return;
        }

        midiStreamPlayer.MPTK_Channels[channel].ForcedPreset = preset;
    }

    /// <summary>
    /// Per-role gain trim: pure MPTK channel volume multiply (doesn't affect note velocity/timbre).
    /// </summary>
    public void SetVolume(float volume)
    {
        _volume = volume;

        if (midiStreamPlayer == null)
            return;

        if (midiStreamPlayer.MPTK_Channels == null)
        {
            Debug.LogWarning(
                $"[MidiVoice] MPTK_Channels not ready yet on {name}; " +
                $"volume {volume} not applied.");
            return;
        }

        if (channel < 0 || channel >= midiStreamPlayer.MPTK_Channels.Length)
        {
            Debug.LogWarning(
                $"[MidiVoice] Channel out of range on {name}: channel={channel}, " +
                $"channelsLength={midiStreamPlayer.MPTK_Channels.Length}. " +
                $"Volume {volume} not applied.");
            return;
        }

        if (midiStreamPlayer.MPTK_Channels[channel] == null)
        {
            Debug.LogWarning(
                $"[MidiVoice] Channel slot {channel} is null on {name}; " +
                $"volume {volume} not applied.");
            return;
        }

        midiStreamPlayer.MPTK_Channels[channel].Volume = volume;
    }

    private Coroutine _rampInCoroutine;

    /// <summary>
    /// Fades this voice's channel volume up from a low starting fraction to targetVolume
    /// over duration seconds. Used to soften a track's first-ever SuperNode instant-fill
    /// instead of snapping straight to full volume.
    /// </summary>
    public void RampInVolume(float targetVolume, float duration, float startFraction = 0.15f)
    {
        if (_rampInCoroutine != null) StopCoroutine(_rampInCoroutine);
        _rampInCoroutine = StartCoroutine(RampInVolumeRoutine(targetVolume, duration, startFraction));
    }

    private IEnumerator RampInVolumeRoutine(float targetVolume, float duration, float startFraction)
    {
        float start = targetVolume * startFraction;
        float elapsed = 0f;
        SetVolume(start);

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            SetVolume(Mathf.Lerp(start, targetVolume, elapsed / duration));
            yield return null;
        }

        SetVolume(targetVolume);
        _rampInCoroutine = null;
    }

    /// <summary>
    /// Routes this voice's AudioSource to the given mixer group (per-role bus). No-op if group is null.
    /// </summary>
    public void SetMixerGroup(AudioMixerGroup group)
    {
        if (group == null || midiStreamPlayer == null || midiStreamPlayer.CoreAudioSource == null)
            return;

        midiStreamPlayer.CoreAudioSource.outputAudioMixerGroup = group;
    }

    public void Bind(
        MidiStreamPlayer player,
        DrumTrack drums,
        System.Func<float> remainingActiveWindowSec,
        int initialPreset = 0)
    {
        midiStreamPlayer = player;
        drumTrack = drums;
        _remainingActiveWindowSec = remainingActiveWindowSec;
        preset = initialPreset;

        // Apply cached preset now if the player is ready.
        SetPreset(preset);
    }
    /// <summary>
    /// Plays a normal note. Expects duration in TICKS (MPTK convention),
    /// converts to ms using drum BPM, and trims to the remaining audible window.
    /// </summary>
    private void PlayNoteTicks(int note, int durationTicks, float velocity, bool trimToActiveWindow = true)
    {
        if (GameFlowManager.VerboseLogging) Debug.Log($"[NOTE TICK] Value: {note} Duration: {durationTicks} Velocity: {velocity} ");
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
        if (trimToActiveWindow && _remainingActiveWindowSec != null)
        {
            float remainSec = _remainingActiveWindowSec();
            if (!float.IsPositiveInfinity(remainSec) && remainSec < float.MaxValue)
            {
                int maxMs = Mathf.Max(10, Mathf.FloorToInt(remainSec * 1000f));
                if (durationMs != Mathf.Min(durationMs, maxMs))
                    if (GameFlowManager.VerboseLogging) Debug.Log($"[MIDI:TRIM] note={note} durationMs={durationMs} → {maxMs} (remainSec={remainSec:F3})");
                durationMs = Mathf.Min(durationMs, maxMs);
            }
        }
        midiStreamPlayer.MPTK_Channels[channel].ForcedPreset = preset;
        midiStreamPlayer.MPTK_Channels[channel].ForcedBank = bank;
        midiStreamPlayer.MPTK_Channels[channel].Volume = _volume;

        float v127 = (velocity <= 1.01f) ? (velocity * 127f) : velocity;

        var noteOn = new MPTKEvent
        {
            Command  = MPTKCommand.NoteOn,
            Value    = note,
            Channel  = channel,
            Duration = durationMs,
            Velocity = Mathf.Clamp(Mathf.RoundToInt(v127), 1, 127),
        };

        midiStreamPlayer.MPTK_PlayEvent(noteOn);
    }

// Keep your old signature working everywhere else:
    public void PlayNoteTicks(int note, int durationTicks, float velocity)
    {
        PlayNoteTicks(note, durationTicks, velocity, trimToActiveWindow: true);
    }
    public void SetMidiStreamPlayer(MidiPlayerTK.MidiStreamPlayer player)
    {
        if (player != null) midiStreamPlayer = player;
    }
    
    /// <summary>
    /// Temporary override program for one-shot FX without permanently changing the voice.
    /// </summary>
    public void PlayOneShotMs127(int note, int durationMs, int velocity127, int overridePreset, int overrideBank)
    {
        if (midiStreamPlayer == null)
            return;

        // Set preset/bank unconditionally before queuing — MPTK processes events
        // asynchronously, so restoring after MPTK_PlayEvent would clobber the
        // override before the note is actually rendered.
        midiStreamPlayer.MPTK_Channels[channel].ForcedPreset = overridePreset;
        midiStreamPlayer.MPTK_Channels[channel].ForcedBank   = overrideBank;
        midiStreamPlayer.MPTK_Channels[channel].Volume       = _volume;

        var ev = new MPTKEvent
        {
            Command  = MPTKCommand.NoteOn,
            Value    = Mathf.Clamp(note, 0, 127),
            Channel  = channel,
            Duration = Mathf.Max(1, durationMs),
            Velocity = Mathf.Clamp(velocity127, 1, 127),
        };

        midiStreamPlayer.MPTK_PlayEvent(ev);
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
        midiStreamPlayer.MPTK_Channels[channel].Volume = _volume;

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
