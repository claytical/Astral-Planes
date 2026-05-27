using System.Collections.Generic;
using MidiPlayerTK;
using UnityEngine;

/// <summary>
/// Plays back a saved MotifSnapshot as a looping MIDI preview in the PhaseLibraryCarousel.
/// Uses per-role MIDI channels so each instrument sounds correct, and resolves
/// timing from the MotifProfile rather than requiring a DrumTrack in the scene.
/// </summary>
[DisallowMultipleComponent]
public class RingPreviewPlayer : MonoBehaviour
{
    [SerializeField] private MidiStreamPlayer midiStreamPlayer;
    [SerializeField] private PhaseLibrary phaseLibrary;

    // Bass=0, Harmony=1, Lead=2, Groove=3, None=4
    private static readonly Dictionary<MusicalRole, int> RoleChannel = new()
    {
        { MusicalRole.Bass,    0 },
        { MusicalRole.Harmony, 1 },
        { MusicalRole.Lead,    2 },
        { MusicalRole.Groove,  3 },
        { MusicalRole.None,    4 },
    };

    private bool   _playing;
    private int    _totalSteps;
    private double _loopStartDsp;
    private float  _loopLengthSec;
    private float  _stepDurationSec;
    private int    _lastStep = -1;

    // [step] → list of (midiNote, midiChannel, vel127, durationMs)
    private List<List<(int note, int channel, int vel127, int durationMs)>> _stepTable;

    // Preset per channel, cached so FireStep can re-assert without a library call each frame.
    private int[] _channelPresets;

    public void Play(MotifSnapshot snap)
    {
        Stop();

        if (snap == null || snap.CollectedNotes == null || snap.CollectedNotes.Count == 0 || snap.TotalSteps <= 0)
            return;

        if (phaseLibrary == null)
        {
            Debug.LogWarning("[RingPreviewPlayer] phaseLibrary not assigned.");
            return;
        }

        if (snap.PhaseIndex < 0 || snap.PhaseIndex >= phaseLibrary.phases.Count)
        {
            Debug.LogWarning($"[RingPreviewPlayer] PhaseIndex {snap.PhaseIndex} out of range.");
            return;
        }

        var phase = phaseLibrary.phases[snap.PhaseIndex];
        if (snap.MotifIndex < 0 || snap.MotifIndex >= phase.motifs.Count)
        {
            Debug.LogWarning($"[RingPreviewPlayer] MotifIndex {snap.MotifIndex} out of range.");
            return;
        }

        var motif = phase.motifs[snap.MotifIndex];
        if (motif == null)
            return;

        float bpm = motif.bpm > 0f ? motif.bpm : 120f;
        int stepsPerLoop = motif.stepsPerLoop > 0 ? motif.stepsPerLoop : 16;

        AudioClip drumClip = null;
        if (motif.intensityDrumLoops != null)
            foreach (var c in motif.intensityDrumLoops) { if (c != null) { drumClip = c; break; } }
        if (drumClip == null && motif.entryDrumLoops != null)
            foreach (var c in motif.entryDrumLoops) { if (c != null) { drumClip = c; break; } }

        _stepDurationSec = drumClip != null
            ? drumClip.length / stepsPerLoop
            : 60f / bpm * 4f / stepsPerLoop;

        // Build (binIndex, trackColor) → role lookup from TrackBins.
        var roleByBinColor = new Dictionary<(int, Color), MusicalRole>();
        if (snap.TrackBins != null)
        {
            foreach (var tb in snap.TrackBins)
            {
                var key = (tb.BinIndex, tb.TrackColor);
                if (!roleByBinColor.ContainsKey(key))
                    roleByBinColor[key] = tb.Role;
            }
        }

        // Build per-role sorted ring lists (each role's BinIndexes in ascending order).
        var roleSortedBins = new Dictionary<MusicalRole, List<int>>();
        if (snap.TrackBins != null)
        {
            foreach (var tb in snap.TrackBins)
            {
                if (!roleSortedBins.TryGetValue(tb.Role, out var list))
                    roleSortedBins[tb.Role] = list = new List<int>();
                if (!list.Contains(tb.BinIndex)) list.Add(tb.BinIndex);
            }
            foreach (var kvp in roleSortedBins) kvp.Value.Sort();
        }

        // Loop length = max ring count across all roles, capped at 5 (4 primary + 1 alternate).
        int maxBinsPerRole = 0;
        foreach (var kvp in roleSortedBins)
            maxBinsPerRole = Mathf.Max(maxBinsPerRole, kvp.Value.Count);
        int playbackBinCount = Mathf.Min(maxBinsPerRole > 0 ? maxBinsPerRole : 1, 5);

        _totalSteps    = playbackBinCount * stepsPerLoop;
        _loopLengthSec = _totalSteps * _stepDurationSec;

        // Cache presets per channel — use the motif's own roleProfile, fall back to library.
        _channelPresets = new int[16];
        foreach (var kvp in RoleChannel)
        {
            MusicalRoleProfile roleProf = null;
            var cfg = motif.GetConfigForRoleAtBin(kvp.Key, 0, 1);
            if (cfg?.roleProfile != null)
                roleProf = cfg.roleProfile;
            else
                roleProf = MusicalRoleProfileLibrary.GetProfile(kvp.Key);

            if (roleProf != null && kvp.Value < 16)
            {
                _channelPresets[kvp.Value] = roleProf.midiPreset;
                Debug.Log($"[PREVIEW] Channel {kvp.Value} ({kvp.Key}) Preset {roleProf.midiPreset}");
            }
        }

        // Build step table: spread notes across sequential bins and retune to per-bin chord.
        _stepTable = new List<List<(int, int, int, int)>>(_totalSteps);
        for (int i = 0; i < _totalSteps; i++)
            _stepTable.Add(new List<(int, int, int, int)>());

        foreach (var entry in snap.CollectedNotes)
        {
            roleByBinColor.TryGetValue((entry.BinIndex, entry.TrackColor), out var role);

            if (!roleSortedBins.TryGetValue(role, out var sortedBins)) continue;
            int ringIdx = sortedBins.IndexOf(entry.BinIndex);
            if (ringIdx < 0 || ringIdx >= playbackBinCount) continue;

            int withinBinStep = entry.Step % stepsPerLoop;
            int newStep       = Mathf.Clamp(ringIdx * stepsPerLoop + withinBinStep, 0, _totalSteps - 1);

            Chord chord      = GetChordForBin(ringIdx, motif.chordProgression, motif.alternateChordProgressionProfile);
            int   retunedNote = SnapNoteToChord(entry.Note, chord);

            int ch = RoleChannel.TryGetValue(role, out int rc) ? rc : 4;

            float v01  = entry.Velocity > 1.01f ? entry.Velocity / 127f : entry.Velocity;
            int vel127 = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(40f, 127f, v01)), 40, 100);

            _stepTable[newStep].Add((Mathf.Clamp(retunedNote, 0, 127), ch, vel127, 0));
        }

        // Post-process: assign per-note duration = distance to next onset on the same channel.
        var channelNoteSteps = new Dictionary<int, List<int>>();
        for (int s = 0; s < _totalSteps; s++)
            foreach (var e in _stepTable[s])
            {
                if (!channelNoteSteps.TryGetValue(e.channel, out var cList))
                    channelNoteSteps[e.channel] = cList = new List<int>();
                if (cList.Count == 0 || cList[cList.Count - 1] != s)
                    cList.Add(s);
            }

        int floorMs = Mathf.Max(10, Mathf.RoundToInt(_stepDurationSec * 200f));
        foreach (var kvp in channelNoteSteps)
        {
            int ch    = kvp.Key;
            var steps = kvp.Value;
            for (int i = 0; i < steps.Count; i++)
            {
                int thisStep = steps[i];
                int nextStep = steps[(i + 1) % steps.Count];
                int dist = nextStep > thisStep
                    ? nextStep - thisStep
                    : _totalSteps - thisStep + nextStep;
                if (dist == 0) dist = _totalSteps;
                int dMs = Mathf.Max(floorMs,
                    Mathf.RoundToInt(dist * _stepDurationSec * 1000f * 0.95f));

                var row = _stepTable[thisStep];
                for (int j = 0; j < row.Count; j++)
                {
                    var n = row[j];
                    if (n.channel == ch)
                        row[j] = (n.note, n.channel, n.vel127, dMs);
                }
            }
        }

        PrimeChannels();

        _loopStartDsp = AudioSettings.dspTime;
        _lastStep     = -1;
        _playing      = true;
    }

    public void Stop()
    {
        _playing      = false;
        _lastStep     = -1;
        _loopStartDsp = 0.0;
        _stepTable    = null;
    }

    private void Update()
    {
        if (!_playing || midiStreamPlayer == null || _stepTable == null) return;

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
    }

    private void FireStep(int step)
    {
        var notes = _stepTable[step];
        if (notes.Count == 0) return;

        PrimeChannels();

        foreach (var (note, ch, vel127, durationMs) in notes)
        {
            midiStreamPlayer.MPTK_PlayEvent(new MPTKEvent
            {
                Command  = MPTKCommand.NoteOn,
                Value    = note,
                Channel  = ch,
                Duration = durationMs,
                Velocity = vel127,
            });
        }
    }

    private static Chord GetChordForBin(int bin,
        ChordProgressionProfile primary, ChordProgressionProfile alternate)
    {
        ChordProgressionProfile prog = null;
        int localBin = bin;

        if (bin >= 4 && alternate != null && alternate.chordSequence?.Count > 0)
        {
            prog     = alternate;
            localBin = bin - 4;
        }
        else if (primary != null && primary.chordSequence?.Count > 0)
        {
            prog = primary;
        }

        if (prog == null) return default;
        return prog.chordSequence[localBin % prog.chordSequence.Count];
    }

    private static int SnapNoteToChord(int midiNote, Chord chord)
    {
        if (chord.intervals == null || chord.intervals.Count == 0) return midiNote;
        int best = midiNote, bestDist = int.MaxValue;
        for (int oct = -2; oct <= 8; oct++)
            foreach (int interval in chord.intervals)
            {
                int tone = chord.rootNote + interval + oct * 12;
                if (tone < 21 || tone > 108) continue;
                int d = Mathf.Abs(tone - midiNote);
                if (d < bestDist) { bestDist = d; best = tone; }
            }
        return best;
    }

    private void PrimeChannels()
    {
        if (midiStreamPlayer == null || midiStreamPlayer.MPTK_Channels == null || _channelPresets == null)
            return;

        int len = Mathf.Min(_channelPresets.Length, midiStreamPlayer.MPTK_Channels.Length);
        for (int i = 0; i < len; i++)
        {
            var ch = midiStreamPlayer.MPTK_Channels[i];
            if (ch == null) continue;
            ch.ForcedPreset = _channelPresets[i];
            ch.ForcedBank   = 0;
        }
    }
}
