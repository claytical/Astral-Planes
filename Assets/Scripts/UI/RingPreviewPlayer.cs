using System.Collections;
using System.Collections.Generic;
using MidiPlayerTK;
using UnityEngine;

/// <summary>
/// Plays back a saved MotifSnapshot as a looping MIDI preview in the PhaseLibraryCarousel.
/// Uses per-role MIDI channels so each instrument sounds correct, and resolves
/// timing from the MotifProfile rather than requiring a DrumTrack in the scene.
///
/// Mixes like the live game: each role gets its own MidiStreamPlayer (cloned from the serialized
/// template at Awake) routed to that role's AudioMixConfig bus, and volumeMultiplier is applied as
/// MPTK channel gain (same as MidiVoice.SetGainTrim), not velocity. Can optionally run the motif's
/// drum loops underneath (off by default; the AudioBalanceDebugPanel turns it on).
/// </summary>
[DisallowMultipleComponent]
public class RingPreviewPlayer : MonoBehaviour
{
    [Tooltip("Template player. Cloned once per role that AudioMixConfig maps to a bus; unmapped roles play on it directly.")]
    [SerializeField] private MidiStreamPlayer midiStreamPlayer;
    [SerializeField] private PhaseLibrary phaseLibrary;
    [Tooltip("Same RingGlyphConfig asset the carousel slots' MotifRingGlyphApplicator use — keeps " +
             "OnNoteFired's angle in sync with ringStartAngleOffset so the firework lands on the " +
             "ring's actual (rotated) rim position instead of the un-rotated default.")]
    [SerializeField] private RingGlyphConfig ringGlyphConfig;

    private static Dictionary<MusicalRole, int> RoleChannel => RingPlaybackPlan.RoleChannel;

    private bool   _playing;
    private int    _totalSteps;
    private double _loopStartDsp;
    private float  _loopLengthSec;
    private float  _stepDurationSec;
    private int    _lastStep = -1;
    private int    _stepsPerLoop;
    private int    _currentBinIndex = -1;

    /// <summary>Fired when playback crosses into a new bin (or when playback stops, with -1).</summary>
    public event System.Action<int> OnBinIndexChanged;

    /// <summary>Fired at most once per role per step that has a note, giving the ring-local angle
    /// (radians, same convention as the note-dip placement in MotifRingGlyphApplicator) where that
    /// role's ring should show it — used to spawn a positional VFX on the matching ring.</summary>
    public event System.Action<MusicalRole, int, float> OnNoteFired;

    /// <summary>Fired once per role per step with a note: (role, binIndex, localStep within the bin,
    /// the step's scheduled dsp time, longest note duration in seconds at that step). Drives the
    /// ring dip pulse, which swells for the note's sounding length.</summary>
    public event System.Action<MusicalRole, int, int, double, float> OnNotePulse;

    /// <summary>Fired ahead of a role's next note on its ring: (role, binIndex, localStep of that
    /// note, its scheduled dsp time, dsp time the ring starts moving — the previous note's start). Lets the ring sweep into place before
    /// the pulse starts.</summary>
    public event System.Action<MusicalRole, int, int, double, double> OnNoteApproach;

    // [step] → notes starting on that step (from RingPlaybackPlan).
    private List<List<RingPlaybackPlan.NoteEvent>> _stepTable;
    private RingPlaybackPlan _plan;

    // Preset per channel, cached so FireStep can re-assert without a library call each frame.
    private int[] _channelPresets;

    // ── Per-role players (mixer routing) ─────────────────────────────────────────
    private readonly Dictionary<MusicalRole, MidiStreamPlayer> _rolePlayers = new();
    private readonly List<MidiStreamPlayer> _allPlayers = new();
    private readonly List<GameObject> _clonedPlayers = new();

    // ── Gain trim ────────────────────────────────────────────────────────────────
    // The plan's per-role profile volumeMultiplier is read live every prime so balance edits are
    // heard on the next note; the snapshot's saved trim is the fallback.
    private readonly List<MusicalRole> _activeRoles = new();

    // ── Drum loop decks ──────────────────────────────────────────────────────────
    private const double kScheduleLeadSec = 0.1;
    private AudioSource _drumActive, _drumIdle;
    private readonly List<AudioClip> _drumLoops = new();
    private MotifProfile _motif;
    private int _drumLoopIndex;
    private bool _drumsEnabled;

    public MotifProfile CurrentMotif => _motif;
    public bool IsPlaying => _playing;

    /// <summary>dsp time of step 0 of the current playback (0 when stopped).</summary>
    public double LoopStartDsp => _loopStartDsp;
    public float LoopLengthSec => _loopLengthSec;

    /// <summary>The MIDI player each role sounds on (a per-bus clone, or the shared template).</summary>
    public MidiStreamPlayer GetRolePlayer(MusicalRole role) => PlayerFor(role);

    /// <summary>Roles that have notes in the current snapshot, in RoleChannel order.</summary>
    public IReadOnlyList<MusicalRole> ActiveRoles => _activeRoles;

    /// <summary>The profile whose volumeMultiplier is applied to this role's preview channel.</summary>
    public MusicalRoleProfile GetRoleProfile(MusicalRole role) =>
        _plan != null && _plan.RoleProfiles.TryGetValue(role, out var p) ? p : null;

    /// <summary>The current motif's drum loops: entryDrumLoops then intensityClipTags, de-duplicated.</summary>
    public IReadOnlyList<AudioClip> DrumLoops => _drumLoops;
    public int DrumLoopIndex => _drumLoopIndex;

    /// <summary>Drum loop under the preview. Off by default so the player-facing carousel is MIDI only.</summary>
    public bool DrumsEnabled
    {
        get => _drumsEnabled;
        set
        {
            if (_drumsEnabled == value) return;
            _drumsEnabled = value;
            if (value && _playing) ScheduleDrumLoop(_drumLoopIndex);
            else if (!value) StopDrums();
        }
    }

    /// <summary>When set, each newly previewed motif starts on a medium/high-intensity loop
    /// (PickHighIntensityLoopIndex) instead of its first entry loop. Driven by the player beat toggle.</summary>
    public bool PreferHighIntensity { get; private set; }

    /// <summary>Player-facing beat toggle: on = drums under the preview on a medium/high-intensity loop.</summary>
    public void SetBeat(bool on)
    {
        PreferHighIntensity = on;
        if (on)
        {
            int idx = PickHighIntensityLoopIndex();
            if (idx >= 0) _drumLoopIndex = idx;
        }
        DrumsEnabled = on;
    }

    /// <summary>
    /// A random clip from the back half (ceil(n/2)) of the motif's intensityClipTags — the array is
    /// authored low → high intensity — as an index into DrumLoops. Falls back to the last loop when
    /// the motif has no intensity tags; -1 when it has no loops at all.
    /// </summary>
    public int PickHighIntensityLoopIndex()
    {
        if (_drumLoops.Count == 0) return -1;

        var tagged = new List<AudioClip>();
        if (_motif != null && _motif.intensityClipTags != null)
            foreach (var t in _motif.intensityClipTags)
                if (t.clip != null) tagged.Add(t.clip);

        if (tagged.Count == 0) return _drumLoops.Count - 1;

        int take  = (tagged.Count + 1) / 2;
        var clip  = tagged[tagged.Count - take + Random.Range(0, take)];
        int index = _drumLoops.IndexOf(clip);
        return index >= 0 ? index : _drumLoops.Count - 1;
    }

    public void SetDrumLoop(int index)
    {
        if (_drumLoops.Count == 0) return;
        _drumLoopIndex = ((index % _drumLoops.Count) + _drumLoops.Count) % _drumLoops.Count;
        if (_drumsEnabled && _playing) ScheduleDrumLoop(_drumLoopIndex);
    }

    /// <summary>Re-applies the current motif's drum level (AudioMixConfig.drumVolume × motif.drumVolumeMultiplier) to both decks.</summary>
    public void RefreshDrumVolume()
    {
        float volume = AudioMixConfig.DrumVolumeOrDefault(_motif, AudioMixConfig.DefaultDrumVolume);
        if (_drumActive != null) _drumActive.volume = volume;
        if (_drumIdle   != null) _drumIdle.volume   = volume;
    }

    private void Awake()
    {
        BuildRolePlayers();
        BuildDrumDecks();
    }

    private void OnDestroy()
    {
        foreach (var go in _clonedPlayers)
            if (go != null) Destroy(go);
        _clonedPlayers.Clear();
    }

    // Live tracks each own a MidiStreamPlayer routed to their role's bus; the preview needs the
    // same, because one AudioSource can only feed one mixer group.
    private void BuildRolePlayers()
    {
        _rolePlayers.Clear();
        _allPlayers.Clear();
        if (midiStreamPlayer == null) return;

        _allPlayers.Add(midiStreamPlayer);
        var mix = AudioMixConfig.Instance;
        foreach (var kvp in RoleChannel)
        {
            var group = mix != null ? mix.GetGroup(kvp.Key) : null;
            if (group == null) { _rolePlayers[kvp.Key] = midiStreamPlayer; continue; }

            var clone = Instantiate(midiStreamPlayer, midiStreamPlayer.transform.parent);
            clone.name = $"{midiStreamPlayer.name} [{kvp.Key}]";
            var src = clone.GetComponent<AudioSource>();
            if (src != null) src.outputAudioMixerGroup = group;

            _rolePlayers[kvp.Key] = clone;
            _allPlayers.Add(clone);
            _clonedPlayers.Add(clone.gameObject);
        }
    }

    private MidiStreamPlayer PlayerFor(MusicalRole role) =>
        _rolePlayers.TryGetValue(role, out var p) && p != null ? p : midiStreamPlayer;

    private void BuildDrumDecks()
    {
        var mix = AudioMixConfig.Instance;
        _drumActive = MakeDeck(mix);
        _drumIdle   = MakeDeck(mix);
    }

    private AudioSource MakeDeck(AudioMixConfig mix)
    {
        var src = gameObject.AddComponent<AudioSource>();
        src.playOnAwake  = false;
        src.loop         = true;
        src.spatialBlend = 0f;
        src.volume       = mix != null ? mix.drumVolume : AudioMixConfig.DefaultDrumVolume;
        if (mix != null && mix.drumsGroup != null) src.outputAudioMixerGroup = mix.drumsGroup;
        return src;
    }

    private void RebuildDrumLoops(MotifProfile motif)
    {
        if (motif == _motif && _drumLoops.Count > 0) return; // same motif: keep the chosen loop
        _drumLoops.Clear();
        _drumLoopIndex = 0;
        if (motif == null) return;

        if (motif.entryDrumLoops != null)
            foreach (var c in motif.entryDrumLoops)
                if (c != null && !_drumLoops.Contains(c)) _drumLoops.Add(c);
        if (motif.intensityClipTags != null)
            foreach (var t in motif.intensityClipTags)
                if (t.clip != null && !_drumLoops.Contains(t.clip)) _drumLoops.Add(t.clip);

        // Drum clips import with preloadAudioData off (compressed). PlayScheduled on an undecoded
        // clip starts late by the decode time, so decode them all up front — same as DrumTrack.PreloadClip.
        foreach (var c in _drumLoops)
            if (c.loadState == AudioDataLoadState.Unloaded) c.LoadAudioData();
    }

    // Loop waiting for its clip to finish decoding before it can be scheduled sample-accurately.
    private int _pendingDrumIndex = -1;

    private void TickPendingDrumLoop()
    {
        if (_pendingDrumIndex < 0) return;
        if (!_drumsEnabled || !_playing) { _pendingDrumIndex = -1; return; }
        var clip = _pendingDrumIndex < _drumLoops.Count ? _drumLoops[_pendingDrumIndex] : null;
        if (clip == null || clip.loadState == AudioDataLoadState.Failed) { _pendingDrumIndex = -1; return; }
        if (clip.loadState != AudioDataLoadState.Loaded) return;

        int idx = _pendingDrumIndex;
        _pendingDrumIndex = -1;
        ScheduleDrumLoop(idx);
    }

    // Starts loop `index` on the idle deck at the next point on that clip's own length grid,
    // measured from the preview's step 0, so it lands phase-aligned with the MIDI; the active deck
    // is cut at the same instant (no gap, no overlap).
    private void ScheduleDrumLoop(int index)
    {
        if (index < 0 || index >= _drumLoops.Count || _drumIdle == null) return;
        var clip = _drumLoops[index];
        if (clip == null || clip.length <= 0f) return;

        if (clip.loadState != AudioDataLoadState.Loaded)
        {
            if (clip.loadState == AudioDataLoadState.Unloaded) clip.LoadAudioData();
            _pendingDrumIndex = index; // TickPendingDrumLoop schedules it once decoded
            return;
        }
        _pendingDrumIndex = -1;

        double now = AudioSettings.dspTime + kScheduleLeadSec;
        double at  = _loopStartDsp;
        if (now > at)
            at = _loopStartDsp + System.Math.Ceiling((now - _loopStartDsp) / clip.length) * clip.length;

        _drumIdle.Stop();
        _drumIdle.clip = clip;
        _drumIdle.loop = true;
        _drumIdle.PlayScheduled(at);
        if (_drumActive != null && _drumActive.isPlaying) _drumActive.SetScheduledEndTime(at);

        (_drumActive, _drumIdle) = (_drumIdle, _drumActive);
    }

    private void StopDrums()
    {
        _pendingDrumIndex = -1;
        if (_drumActive != null) _drumActive.Stop();
        if (_drumIdle   != null) _drumIdle.Stop();
    }

    // Live path parity: MusicalRoleProfile.volumeMultiplier → MPTK channel gain.
    private float TrimFor(MusicalRole role) => _plan != null ? _plan.TrimFor(role) : 1f;

    public void Play(MotifSnapshot snap)
    {
        Stop();

        var plan = RingPlaybackPlan.Build(snap, phaseLibrary);
        if (plan == null) return;
        var motif = plan.Motif;

        _stepDurationSec = plan.StepDurationSec;
        _totalSteps      = plan.TotalSteps;
        _loopLengthSec   = plan.LoopLengthSec;
        _stepsPerLoop    = plan.StepsPerLoop;

        bool motifChanged = motif != _motif;
        RebuildDrumLoops(motif);
        _motif = motif;
        if (motifChanged && PreferHighIntensity) _drumLoopIndex = Mathf.Max(0, PickHighIntensityLoopIndex());
        RefreshDrumVolume();

        _plan           = plan;
        _channelPresets = plan.ChannelPresets;
        _stepTable      = plan.Steps;

        _activeRoles.Clear();
        _activeRoles.AddRange(plan.ActiveRoles);

        PrimeChannels();

        // Small lead so the drum deck can be PlayScheduled sample-accurately on step 0;
        // Update() waits while elapsed < 0.
        _loopStartDsp    = AudioSettings.dspTime + kScheduleLeadSec;
        _lastStep        = -1;
        _currentBinIndex = -1;
        _playing         = true;

        if (_drumsEnabled) ScheduleDrumLoop(_drumLoopIndex);
    }

    /// <summary>Stops scheduling new notes but leaves sounding ones to finish their own duration and
    /// release (unlike Stop, which sends NoteOff to everything). Used by RingAudioExporter for the tail.</summary>
    public void StopFiring()
    {
        _playing = false;
        StopDrums();
    }

    public void Stop()
    {
        _playing        = false;
        _approachPrimed = false;
        _lastStep       = -1;
        _loopStartDsp = 0.0;
        _stepTable    = null;

        // Send NoteOff for any still-sounding notes so the synth's authored release plays out
        // rather than being left to ring indefinitely.
        foreach (var p in _allPlayers)
            if (p != null) p.MPTK_ClearAllSound();
        StopDrums();

        _currentBinIndex = -1;
        OnBinIndexChanged?.Invoke(-1);
    }

    // Release time for MPTK_ClearAllSound's NoteOff is unbounded (its own doc: "can take a few
    // seconds") — waiting it out isn't reliable. Fading CoreAudioSource's own volume to zero
    // guarantees no residual amplitude by the time the caller destroys this GameObject via a
    // scene unload, regardless of how long the synth's release envelope actually takes.
    private const float kStopFadeSec = 0.08f;

    public IEnumerator StopAndFadeRoutine()
    {
        Stop();

        var srcs = new List<AudioSource>();
        var startVols = new List<float>();
        foreach (var p in _allPlayers)
        {
            var src = p != null ? p.CoreAudioSource : null;
            if (src == null) continue;
            srcs.Add(src);
            startVols.Add(src.volume);
        }
        if (srcs.Count == 0) yield break;

        float t = 0f;
        while (t < kStopFadeSec)
        {
            t += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(t / kStopFadeSec);
            for (int i = 0; i < srcs.Count; i++)
                if (srcs[i] != null) srcs[i].volume = Mathf.Lerp(startVols[i], 0f, k);
            yield return null;
        }
        foreach (var src in srcs)
            if (src != null) src.volume = 0f;
    }

    private void Update()
    {
        TickPendingDrumLoop();
        if (!_playing || _allPlayers.Count == 0 || _stepTable == null) return;

        double elapsed = AudioSettings.dspTime - _loopStartDsp;
        if (elapsed < 0.0) return;

        double loopPos   = elapsed % _loopLengthSec;
        int    target    = Mathf.Clamp(Mathf.FloorToInt((float)(loopPos / _stepDurationSec)), 0, _totalSteps - 1);

        if (_lastStep == -1)
        {
            FireStep(target);
        }
        else if (target < _lastStep)
        {
            // Loop boundary crossed — play tail then head.
            for (int s = _lastStep + 1; s < _totalSteps; s++) FireStep(s);
            for (int s = 0; s <= target; s++) FireStep(s);
        }
        else
        {
            for (int s = _lastStep + 1; s <= target; s++) FireStep(s);
        }

        _lastStep = target;

        int bin = target / _stepsPerLoop;
        if (bin != _currentBinIndex)
        {
            _currentBinIndex = bin;
            OnBinIndexChanged?.Invoke(bin);
        }
    }

    // Longest note at this step for a role — a chord shares one ring dip. Scans in place so the
    // per-step hot path doesn't allocate.
    private static int LongestDurationMs(List<RingPlaybackPlan.NoteEvent> notes, MusicalRole role)
    {
        int longest = 0;
        for (int i = 0; i < notes.Count; i++)
            if (notes[i].role == role && notes[i].durationMs > longest) longest = notes[i].durationMs;
        return longest;
    }

    // Reused every step (cleared at the top of FireStep) instead of allocating per step.
    private readonly HashSet<MusicalRole> _firedRoles = new();

    // The step's scheduled time on the preview clock (not this frame's time).
    private double StepStartDsp(int step)
    {
        double stepOffsetSec = step * (double)_stepDurationSec;
        double loopsElapsed  = System.Math.Floor((AudioSettings.dspTime - _loopStartDsp - stepOffsetSec) / _loopLengthSec);
        return _loopStartDsp + loopsElapsed * _loopLengthSec + stepOffsetSec;
    }

    private static bool HasRole(List<RingPlaybackPlan.NoteEvent> notes, MusicalRole role)
    {
        for (int i = 0; i < notes.Count; i++) if (notes[i].role == role) return true;
        return false;
    }

    // Playback just started: each ring (role, bin) gets an approach to its first note, free to
    // move from now, so every ring begins its slow sweep to 12 straight away. Later loops are
    // covered by FireNextApproach wrapping round.
    private void FirePlaybackStartApproaches(int step)
    {
        double nowStepDsp = StepStartDsp(step);
        _approachRings.Clear();
        for (int s = 0; s < _totalSteps; s++)
            foreach (var n in _stepTable[s])
            {
                int bin = s / _stepsPerLoop;
                if (!_approachRings.Add((n.role, bin))) continue;
                int ahead = s >= step ? s - step : s + _totalSteps - step;
                OnNoteApproach.Invoke(n.role, bin, s % _stepsPerLoop,
                    nowStepDsp + ahead * (double)_stepDurationSec, nowStepDsp);
            }
    }

    // After a role's note at `step`, announce its next note on the same ring (same role, same bin).
    // With none later in the bin it wraps to the ring's first note in the next loop, so the ring
    // starts turning as soon as this pulse ends and takes the whole idle gap to get back to 12.
    private void FireNextApproach(int step, MusicalRole role, double stepStartDsp)
    {
        int binStart = step / _stepsPerLoop * _stepsPerLoop;
        int binEnd   = binStart + _stepsPerLoop;
        for (int s = step + 1; s < binEnd + (step - binStart); s++)
        {
            int idx = s < binEnd ? s : binStart + (s - binEnd);        // wraps within the bin
            if (!HasRole(_stepTable[idx], role)) continue;
            int ahead = s < binEnd ? s - step : s - step + (_totalSteps - _stepsPerLoop);
            OnNoteApproach.Invoke(role, binStart / _stepsPerLoop, idx - binStart,
                stepStartDsp + ahead * (double)_stepDurationSec, stepStartDsp);
            return;
        }
    }

    private readonly HashSet<(MusicalRole role, int bin)> _approachRings = new();
    private bool _approachPrimed;

    private void FireStep(int step)
    {
        if (OnNoteApproach != null && !_approachPrimed)
        {
            _approachPrimed = true;
            FirePlaybackStartApproaches(step);
        }

        var notes = _stepTable[step];
        if (notes.Count == 0) return;

        PrimeChannels();

        // Multiple notes (a chord) can share a role at this step — they'd all land at the same
        // ring angle, so only spawn one firework per role per step rather than one per note.
        _firedRoles.Clear();

        foreach (var n in notes)
        {
            var role = n.role;
            PlayerFor(role).MPTK_PlayEvent(new MPTKEvent
            {
                Command  = MPTKCommand.NoteOn,
                Value    = n.note,
                Channel  = n.channel,
                Duration = n.durationMs,
                Velocity = n.vel127,
            });

            if (OnNoteFired == null && OnNotePulse == null && OnNoteApproach == null) continue;
            if (!_firedRoles.Add(role)) continue;

            if (OnNoteApproach != null)
                FireNextApproach(step, role, StepStartDsp(step));

            if (OnNotePulse != null)
            {
                // The step's scheduled time on the preview clock, not this frame's time, so the
                // pulse isn't pushed late by Update's frame quantization.
                double stepOffsetSec = step * (double)_stepDurationSec;
                double loopsElapsed  = System.Math.Floor((AudioSettings.dspTime - _loopStartDsp - stepOffsetSec) / _loopLengthSec);
                double startDsp      = _loopStartDsp + loopsElapsed * _loopLengthSec + stepOffsetSec;
                OnNotePulse.Invoke(role, step / _stepsPerLoop, step % _stepsPerLoop,
                    startDsp, LongestDurationMs(notes, role) / 1000f);
            }

            if (OnNoteFired == null) continue;

            int   localStep = step % _stepsPerLoop;
            float angleRad  = localStep / (float)_stepsPerLoop * Mathf.PI * 2f
                + (ringGlyphConfig != null ? ringGlyphConfig.ringStartAngleOffset : 0f);
            OnNoteFired.Invoke(role, step / _stepsPerLoop, angleRad);
        }
    }

    // Re-asserted before every step (self-heals once MPTK_Channels exists): each role's channel on
    // its own player gets its preset and its profile's live volumeMultiplier as channel gain.
    private void PrimeChannels()
    {
        if (_channelPresets == null) return;

        foreach (var kvp in RoleChannel)
        {
            var player = PlayerFor(kvp.Key);
            if (player == null || player.MPTK_Channels == null) continue;

            int i = kvp.Value;
            if (i < 0 || i >= player.MPTK_Channels.Length || i >= _channelPresets.Length) continue;

            var ch = player.MPTK_Channels[i];
            if (ch == null) continue;
            ch.ForcedPreset = _channelPresets[i];
            ch.ForcedBank   = 0;
            ch.Volume       = TrimFor(kvp.Key);
        }
    }
}
