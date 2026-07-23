using System.Collections;
using System.Collections.Generic;
using MidiPlayerTK;
using UnityEngine;

// Track-lifecycle coordination: cursor/guard resets, motif-boundary hard resets, ship
// configuration, and game-over fade-out. These are mutually-coupled coordinators (touch
// tracks, bin allocation, and the visualizer together) rather than an independently
// isolable concern, so they stay grouped in one partial.
public partial class InstrumentTrackController
{
    private void ResetAllCursorsAndGuards()
    {
        if (tracks == null) return;
        foreach (var t in tracks)
            if (t) t.ResetBinsForPhase();
    }

    public bool AnyExpansionPending()
    {
        if (tracks == null || tracks.Length == 0) return false;
        foreach (var t in tracks)
        {
            if (!t) continue;
            if (t.IsExpansionPending) return true;
        }
        return false;
    }

    private int ForceDestroyAllCollectablesInFlight(string reason)
    {
        int destroyed = 0;
        if (tracks == null) return destroyed;

        foreach (var t in tracks)
        {
            if (t == null) continue;
            destroyed += t.ForceDestroyCollectablesInFlight(reason);
        }

        return destroyed;
    }

    /// <summary>
    /// Single authority entry point for motif boundaries.
    /// This should be called exactly once when a new motif begins (after any bridge/ghost cycle),
    /// and before any new note spawning occurs.
    /// </summary>
    public void BeginNewMotif(string reason = "BeginNewMotif")
    {
        if (GameFlowManager.VerboseLogging) Debug.Log($"[CTRL] BeginNewMotif reason={reason}");
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        _gfm?.activeDrumTrack.ResetBeatSequencingState("InstrumentTrackController/BeginNewMotif");
        // Ensure no in-flight collectables from the prior motif can write late into tracks/visuals.
        ForceDestroyAllCollectablesInFlight(reason);

        // Reset controller-level guards/caches.
        EnsureBinAllocator();
        _binAllocator.ClearAdvanceTokens();
        _loopHash.Clear();

        // Hard reset all tracks (loop content, bins, allocation, burst state).
        if (tracks != null)
        {
            foreach (var t in tracks)
            {
                if (!t) continue;
                t.BeginNewMotifHardClear(reason);
            }
        }

        // Hard reset visuals last (they mirror track state).
        if (noteVisualizer != null)
            noteVisualizer.BeginNewMotif_ClearAll(destroyMarkerGameObjects: true);

        // BeginNewMotifHardClear resets loopMultiplier to 1 on every track, but
        // DrumTrack._binCount still holds the previous motif's committed value. Without
        // an immediate flush, InstrumentTrack.leaderBins = max(stale, 1) stays at the
        // old count for the entire first leader loop, silencing tracks on bars 1-N.
        ResyncLeaderBinsNow();
    }

    public void AdvanceOtherTrackCursors(InstrumentTrack leaderTrack, int by = 1)
    {
        if (tracks == null) return;
        for (int i = 0; i < tracks.Length; i++)
        {
            var t = tracks[i];
            if (!t || t == leaderTrack) continue;
            t.AdvanceBinCursor(by); // silent bin reserved; visuals omitted by design
        }
    }

    public void ConfigureTracksFromShips(List<ShipMusicalProfile> selectedShips)
    {
        AssignVoiceIndices();
        if (tracks != null)
            foreach (var t in tracks)
                if (t != null) t.RefreshRoleColorsFromProfile();
        UpdateVisualizer();
    }

    private void AssignVoiceIndices()
    {
        if (tracks == null) return;
        var roleCounts = new Dictionary<MusicalRole, int>();
        for (int i = 0; i < tracks.Length; i++)
        {
            var t = tracks[i];
            if (t == null) continue;
            if (!roleCounts.TryGetValue(t.assignedRole, out int count)) count = 0;
            t.SetVoiceIndex(count);
            roleCounts[t.assignedRole] = count + 1;
        }
    }

    private IEnumerator FadeOutMidi(MidiStreamPlayer player, float duration)
    {
        float startVolume = player.MPTK_Volume;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            player.MPTK_Volume = Mathf.Lerp(startVolume, 0f, elapsed / duration);
            yield return null;
        }

        player.MPTK_Volume = 0f;
    }

    public void BeginGameOverFade()
    {
        foreach (var track in tracks)
        {
            if (track == null) continue;

            var loopNotes = track.GetPersistentLoopNotes();
            for (int i = 0; i < loopNotes.Count; i++)
            {
                var (step, note, _, velocity, authoredRootMidi) = loopNotes[i];
                int longDuration = 1920; // ≈4 beats (1 bar) at 480 ticks per beat
                loopNotes[i] = (step, note, longDuration, velocity, authoredRootMidi);
            }
            // Start fading out this track's MIDI stream
            if (track.midiStreamPlayer != null)
            {
                track.StartCoroutine(FadeOutMidi(track.midiStreamPlayer, 2f));
            }
        }
    }
}
