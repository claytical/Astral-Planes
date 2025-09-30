// ChordChangeArpeggiator.cs (attach to InstrumentTrackController)
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ChordChangeArpeggiator : MonoBehaviour
{
    public DrumTrack drums;
    public InstrumentTrackController tracks;
    public bool commitHandledByHarmonyDirector = true; 
    private bool _armed;
    private bool _holding;
    private Chord _pendingChord;
    private List<Coroutine> _livePreviews = new();
    private int _lastLoopIdx = -1;
    public System.Action HeldThroughBoundary;

    void OnDisable(){ if (drums != null) drums.OnLoopBoundary -= OnLoopBoundary; }

    void Awake()
    {
        if (drums != null) drums.OnLoopBoundary += OnLoopBoundary;
    }
    public void Bind(DrumTrack d, InstrumentTrackController t)
    {
        if (drums != null) drums.OnLoopBoundary -= OnLoopBoundary;
        drums = d;
        tracks = t;
        if (drums != null) drums.OnLoopBoundary += OnLoopBoundary;
    }
    public void ArmChordWhileHolding(Chord chord)
    {
        if (_armed) return;               // only one at a time
        _armed   = true;
        _holding = true;
        _pendingChord = chord;

        float remain = drums.GetTimeToLoopEnd();
        StartPreviews(chord, remain);
    }
    public void Begin(Chord chord, float secondsRemaining)
    {
        if (_armed || drums == null || tracks == null || tracks.tracks == null) return;
        int loopIdx = Mathf.FloorToInt((float)(AudioSettings.dspTime / Mathf.Max(0.001f, drums.GetLoopLengthInSeconds())));
        if (loopIdx == _lastLoopIdx) return;      // one preview per loop
        _lastLoopIdx = loopIdx;
        _armed = true; _holding = true; _pendingChord = chord;
        StartPreviews(chord, Mathf.Max(0.05f, secondsRemaining));
    }
    public void Cancel()
    {
        _holding = false;
        if (!_armed) return;
        _armed = false;
        StopPreviews();
    }
    
    public void CancelIfReleased()
    {
        _holding = false;
        if (!_armed) return;
        _armed = false;
        StopPreviews();
        // TODO: optionally refund a chord-charge here
    }
    private void OnLoopBoundary()
    {
        if (!_armed) return;
        StopPreviews();
        if (_holding)
        {
            HeldThroughBoundary?.Invoke();
            _armed = false; _holding = false;
            _lastLoopIdx = -1;
        }
        
        // For Option C, HarmonyDirector rotates/commits the chord at the boundary.
        if (_holding && !commitHandledByHarmonyDirector)
        {
            foreach (var t in tracks.tracks)
                t.ApplyChord(_pendingChord, reusePlayerNotes: true);
        }

        _armed = false;
        _holding = false;
    }
    private IEnumerator PreviewArp(InstrumentTrack track, Chord chord, float remainSeconds)
    {
        // Build playable chord tones across the track range
        var tones = new List<int>();
        for (int oct = -2; oct <= 3; oct++)
            foreach (var iv in chord.intervals)
            {
                int n = chord.rootNote + iv + 12 * oct;
                if (n >= track.lowestAllowedNote && n <= track.highestAllowedNote) tones.Add(n);
            }
        if (tones.Count == 0) yield break;

        // Choose how many notes to span based on remaining time (breathes with loop)
        // Longer remain ⇒ fewer notes & longer gaps; Short remain ⇒ more notes packed tightly.
        // Or invert this if you prefer. Here we target ~8th-note density at 120 BPM as a baseline.
        float bpm = drums.drumLoopBPM;
        float eighth = 60f / bpm * 0.5f;                // 8th note seconds (two per beat)
        int noteCount = Mathf.Clamp(Mathf.RoundToInt(remainSeconds / eighth), 3, 16);

        // Time per arpeggio note so we land on the downbeat
        float stepDur = remainSeconds / noteCount;

        // Direction: rise to feel “leading into” change
        tones.Sort();
        int idx = 0;

        float t = 0f;
        while (t < remainSeconds)
        {
            int note = tones[idx % tones.Count];
            // very short blips that scale with stepDur (feel faster near the end)
            int durMs = Mathf.RoundToInt(Mathf.Clamp(stepDur * 1000f * 0.8f, 40f, 300f));
            track.PlayNote(note, durMs, Mathf.Lerp(70f, 115f, (idx % tones.Count) / (float)tones.Count));

            yield return new WaitForSeconds(stepDur);

            idx++;
            t += stepDur;
        }
    }
    private void StartPreviews(Chord chord, float remainSeconds)
    {
        if (tracks == null || tracks.tracks == null) return;
        StopPreviews();
        foreach (var t in tracks.tracks)
            if (t != null)
                _livePreviews.Add(StartCoroutine(PreviewArp(t, chord, remainSeconds)));
    }
    private void StopPreviews()
    {
        foreach (var c in _livePreviews)
            if (c != null) StopCoroutine(c);
        _livePreviews.Clear();
    }

}
