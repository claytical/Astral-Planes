using System;
using UnityEngine;
using MidiPlayerTK;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

public partial class InstrumentTrack
{
    public void RefreshRoleColorsFromProfile(MusicalRoleProfile overrideProfile = null)
    {
        var prof = overrideProfile ?? MusicalRoleProfileLibrary.GetProfile(assignedRole);
        if (prof == null) return;

        trackColor       = prof.GetColorForVoice(voiceIndex);
        trackShadowColor = prof.GetShadowColor();
        preset           = prof.midiPreset;
        midiVoice?.SetPreset(prof.midiPreset);
    }

    void Awake()
    {
        if (!midiVoice) midiVoice = GetComponent<MidiVoice>();
        if (!loopPattern) loopPattern = GetComponent<LoopPattern>() ?? gameObject.AddComponent<LoopPattern>();
        _expansionCtrl = new TrackExpansionController(this);
        _expansionCtrl?.Bind(drumTrack);
        var awakeProf = MusicalRoleProfileLibrary.GetProfile(assignedRole);
        if (midiVoice != null)
        {
            midiVoice.Bind(
                midiStreamPlayer,
                drumTrack,
                RemainingActiveWindowSec,
                awakeProf?.midiPreset ?? 0
            );
        }

        RefreshRoleColorsFromProfile(awakeProf);
    }
    void Start() {
        if (controller == null)
        {
            Debug.LogError($"{gameObject.name} - No InstrumentTrackController assigned!");
            return;
        }

        if (drumTrack == null)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log("No drumtrack assigned!");
            return;
        }
        _waitingForDrumReady = true;
        if (_totalSteps <= 0) _totalSteps = BinSize();
        InitializeBinChords(maxLoopMultiplier);
    }

    void Update() {
        // One-shot replacement for WaitForDrumTrackStartTime()

        if (_waitingForDrumReady)
        {
            bool ready =
                drumTrack != null &&
                drumTrack.GetLoopLengthInSeconds() > 0f &&
                ((drumTrack.leaderStartDspTime > 0.0) || (drumTrack.startDspTime != 0));

            if (ready)
            {
                _totalSteps = drumTrack.totalSteps * loopMultiplier;
                _waitingForDrumReady = false;
            }
            else return;
        }

        if (drumTrack == null) return;
        if (_nextFrameQueue.Count > 0)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[TRK:NEXTFRAME_RUN] track={name} queueCount={_nextFrameQueue.Count} waitingForDrum={_waitingForDrumReady}");
            var count = _nextFrameQueue.Count; // snapshot to avoid reentrancy issues
            for (int i = 0; i < count; i++)
            {
                try { _nextFrameQueue[i]?.Invoke(); }
                catch (System.Exception e) { Debug.LogException(e); }
            }
            _nextFrameQueue.RemoveRange(0, count);
        }
        _expansionCtrl?.Tick(Time.deltaTime);
        // - Drum loop stays the bar clock (binSize steps).
        // - We do NOT stretch bar time when bins increase.
// ----- TRANSPORT (single authority) -----
        if (controller == null) return;

        var tf = controller.GetTransportFrame();

// Defensive clamp: never allow negative barIndex to drive barStart math.
        int barIndex = tf.barIndex;
        int playheadBin = tf.playheadBin;
        int boundarySerial = tf.boundarySerial;

        if (barIndex < 0)
        {
            // If this ever happens again, do the safest thing:
            // treat as start of loop so we don't compute barStart in the past.
            barIndex = 0;
            playheadBin = 0;
        }


// Reset step cursor on bar change within the leader loop.
// boundarySerial handles full-loop resets; this closes the gap where
// targetCurLocal == _lastLocalStep at a bin transition (< guard misses ==).
        if (barIndex != _lastBarIndex)
        {
            _lastLocalStep = -1;
            _lastBarIndex  = barIndex;
        }

// Normalize playheadBin into [0, leaderBins-1].
// ----- CLOCK (single authority: DSP) -----
        double dspNow = AudioSettings.dspTime;

        float clipLen;
        try { clipLen = drumTrack.GetClipLengthInSeconds(); }
        catch
        {
            clipLen = (drumTrack.drumAudioSource != null && drumTrack.drumAudioSource.clip != null)
                ? drumTrack.drumAudioSource.clip.length
                : 0f;
        }
        if (clipLen <= 0f) return;

        int drumSteps = Mathf.Max(1, drumTrack.totalSteps);
        int binSize   = Mathf.Max(1, BinSize());

        double start = (drumTrack.leaderStartDspTime > 0.0) ? drumTrack.leaderStartDspTime : drumTrack.startDspTime;
        if (start <= 0.0) return;

        if (dspNow < start)
        {
            // Don't manufacture a bar/bin; just wait.
            return;
        }

        double barStart = start + (double)barIndex * clipLen;

        double transportStart = (drumTrack.leaderStartDspTime > 0.0) ? drumTrack.leaderStartDspTime : drumTrack.startDspTime;
        double localStart     = drumTrack.startDspTime;


// ----- BAR BOUNDARY COMMIT (cache + step reset) -----
// ----- BOUNDARY COMMIT (cache + step reset) -----
// IMPORTANT:
// barIndex is no longer a reliable monotonic boundary signal once DrumTrack
// advances leaderStartDspTime every effective loop. Use DrumTrack's boundarySerial
// as the authority for cache commits.
        if (boundarySerial != _lastCommittedBoundarySerial)
        {
            _lastCommittedBoundarySerial = boundarySerial;
            _lastCommittedBar = barIndex; // debug / inspector only

            // Always rebuild at boundary: picks up any loopMultiplier/binSize change
            // that ArmCohortsOnLoopBoundary may have applied since the last mid-loop rebuild.
            RebuildLoopCache_FORCE();
            _loopCacheDirtyPending = false;

            // Reset step cursor so step 0 is eligible in the new bar/bin window
            _lastLocalStep = -1;
        }
// ----- STEP INDEX (DSP-derived) -----
        int curStep = GetDspStepIndexInBar(dspNow, barStart, clipLen, drumSteps);
        int targetCurLocal = ((curStep % binSize) + binSize) % binSize;

// ----- PLAYBACK (catch-up deterministically) -----
        // Audio must follow the committed leader bins (transport), not the UI's visual bins.
        int committedLeaderBins = Mathf.Max(1, controller.GetCommittedLeaderBins());
        int leaderBins          = committedLeaderBins;

        // Diagnostic: log on every bar transition.
        if (barIndex != _lastBarIndex)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[TRK:BAR_ENTER] track={name} barIndex={barIndex} " +
                      $"committedLeaderBins={committedLeaderBins} loopMul={loopMultiplier} " +
                      $"playheadBin={playheadBin} leaderStart={drumTrack.leaderStartDspTime:F3} " +
                      $"dsp={AudioSettings.dspTime:F3}");
        }

        // Guard: if barIndex has advanced past the committed leader width, DrumTrack hasn't
        // processed the loop boundary yet this frame (script-execution-order race). Skip note
        // playback entirely — the next frame will use the correct updated transport state.
        // This prevents a 1-bin track from ghosting its bin-0 content into bar 1 of the new
        // expanded loop during the single frame before leaderStartDspTime is advanced.
        if (barIndex >= committedLeaderBins)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[TRK:BAR_GUARD] track={name} barIndex={barIndex} " +
                      $"committedLeaderBins={committedLeaderBins} loopMul={loopMultiplier} SKIPPING");
            _lastLocalStep = targetCurLocal;
            return;
        }

        int playbackBin = WrapIndex(playheadBin, leaderBins);

// Play every missed step exactly once, in order.
        // Guard: if the target wrapped below the last-played step without a bar change
        // (float precision edge at bar boundary), reset the cursor so no steps are skipped.
        if (targetCurLocal < _lastLocalStep)
            _lastLocalStep = -1;

        int startStep = _lastLocalStep + 1;
        if (startStep < 0) startStep = 0;

        for (int s = startStep; s <= targetCurLocal; s++)
        {
            int local = ((s % binSize) + binSize) % binSize;
            PlayLoopedNotesInBin(playbackBin, local, leaderBins);
        }

        _lastLocalStep = targetCurLocal;

        for (int i = spawnedCollectables.Count - 1; i >= 0; i--) {
            var obj = spawnedCollectables[i];
            if (obj == null)
            {
                spawnedCollectables.RemoveAt(i); // clean up dead reference
                continue;
            }
        }
    }
}
