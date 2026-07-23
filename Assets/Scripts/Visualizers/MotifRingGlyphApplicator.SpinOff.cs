using System.Collections;
using System.Linq;
using UnityEngine;

// ── Spin-off / roll-off exit animations ─────────────────────────────────
public partial class MotifRingGlyphApplicator
{
    /// <summary>
    /// Spin the whole record 360° clockwise over <paramref name="spinDuration"/>, then
    /// slide it off the left edge over <paramref name="rollDuration"/>.
    /// </summary>
    public IEnumerator SpinAndRollOffRecordRings(float spinDuration, float rollDuration)
    {
        _recordFadingOut = true;  // stop per-ring rotation coroutines
        Vector3 originalScale = transform.localScale;

        yield return RunSpinRollScale(_recordRings.ToArray(), spinDuration, rollDuration);

        DestroyList(_recordRings);
        _recordFadingOut = false;
        transform.localScale = originalScale;
        transform.rotation = Quaternion.identity;
    }

    /// <summary>
    /// Spin-and-roll-off the accumulated gameplay rings (used at bridge time).
    /// Restores visibility first if the record was hidden after the last deformation.
    /// </summary>
    public IEnumerator SpinAndRollOffActiveRings(float spinDuration, float rollDuration)
    {
        // Prevent per-deformation scale-to-zero while waiting, but keep rings rotating so
        // the record stays animated during the wait (rings look live, not frozen).
        _spinOffPending = true;

        // Motif completion can land anywhere in the currently-playing pass. Wait for the pass
        // to actually finish (all bins played) before doing anything else. This has to happen
        // here, first — not in the caller — because _spinOffPending (set above) is the only
        // thing stopping HideGameplayRingStackAfterLoopBoundary's ordinary per-bin auto-hide
        // from scaling the stack to zero mid-pass; a wait performed before this coroutine even
        // starts wouldn't be covered by that guard.
        var drum = GameFlowManager.Instance?.activeDrumTrack;
        if (drum != null && drum.leaderStartDspTime > 0)
        {
            double loopBoundaryDsp = drum.leaderStartDspTime + drum.GetLoopLengthInSeconds();
            while (AudioSettings.dspTime < loopBoundaryDsp && !_clearingGameplayRings)
                yield return null;
        }

        // Wait for any in-flight deformations so the final ring fully deforms before spin-off.
        // Notes only fire in sync with actual playback (WaitAndLaunchDot waits for the drum to
        // reach each note's real trigger step), so a bin filled with notes whose steps already
        // went by this lap can take up to one full loop pass to finish. Cap the wait at a full
        // loop length (instead of the short spin/roll duration) so a last-instant bin always
        // gets to deform in sync rather than being torn down mid-wait.
        if (_pendingDeformationCount > 0)
        {
            float maxWait = Mathf.Max(spinDuration + rollDuration,
                drum != null ? drum.GetLoopLengthInSeconds() : 0f);
            float waited  = 0f;
            while (_pendingDeformationCount > 0 && waited < maxWait && !_clearingGameplayRings)
            {
                waited += Time.deltaTime;
                yield return null;
            }
        }

        // Deformations settled (or timed out): now stop rotation and begin spin animation.
        _gameplayFadingOut = true;
        _spinOffPending    = false;

        // Restore to full scale — handles both "hidden after deformation" and "mid-fade interrupted".
        transform.localScale = _fitScale.sqrMagnitude > 0.0001f ? _fitScale : Vector3.one;

        yield return RunSpinRollScale(
            _gameplayRings.Concat(_remainingRings).ToArray(), spinDuration, rollDuration);

        DestroyList(_gameplayRings);
        DestroyList(_remainingRings);
        _remainingRings.Clear();
        _gameplayFadingOut       = false;
        _spinOffPending          = false;
        _superNodeMode           = false;
        _pendingDeformationCount = 0;
        transform.localScale     = Vector3.zero;
        transform.rotation       = Quaternion.identity;
    }

    // Shared 3-phase exit animation: spin the parent 360° clockwise over spinDuration
    // (rings spin independently), tilt the X axis to config.tiltXDegrees over rollDuration,
    // then scale the parent to zero over config.scaleDownDuration. Callers own pre-wait,
    // flag setup, ring-list selection, and post-call teardown/scale-reset.
    private IEnumerator RunSpinRollScale(RingEntry[] rings, float spinDuration, float rollDuration)
    {
        float   tiltDeg       = config != null ? config.tiltXDegrees     : 75f;
        float   scaleDur      = config != null ? config.scaleDownDuration : 0.5f;
        float   speedBase     = config != null ? config.rotSpeedMax       : 300f;
        Vector3 originalScale = transform.localScale;

        // Capture each ring's current Z rotation and assign staggered exit speeds.
        // Alternating direction + index spread creates the "deformed but in-sync" wobble.
        var ringZRots  = new float[rings.Length];
        var ringSpeeds = new float[rings.Length];
        for (int i = 0; i < rings.Length; i++)
        {
            ringZRots[i]  = rings[i].Root != null ? rings[i].Root.transform.localEulerAngles.z : 0f;
            float spd     = speedBase * (1f + i * 0.2f);
            ringSpeeds[i] = i % 2 == 0 ? spd : -spd;
        }

        // Phase 1: spin parent 360° clockwise over spinDuration; rings spin independently
        float elapsed = 0f;
        while (elapsed < spinDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / spinDuration);
            transform.rotation = Quaternion.Euler(0f, 0f, -360f * t);
            AdvanceRingZRots(rings, ringZRots, ringSpeeds);
            yield return null;
        }
        transform.rotation = Quaternion.identity;

        // Phase 2: tilt X axis to tiltDeg over rollDuration; rings keep spinning
        elapsed = 0f;
        while (elapsed < rollDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / rollDuration);
            transform.localEulerAngles = new Vector3(Mathf.Lerp(0f, tiltDeg, t), 0f, 0f);
            AdvanceRingZRots(rings, ringZRots, ringSpeeds);
            yield return null;
        }

        // Phase 3: scale to zero; rings keep spinning
        elapsed = 0f;
        while (elapsed < scaleDur)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / scaleDur);
            transform.localScale = Vector3.Lerp(originalScale, Vector3.zero, t);
            AdvanceRingZRots(rings, ringZRots, ringSpeeds);
            yield return null;
        }
    }

    private void AdvanceRingZRots(RingEntry[] rings, float[] zRots, float[] speeds)
    {
        for (int i = 0; i < rings.Length; i++)
        {
            if (rings[i].Root == null) continue;
            zRots[i] += speeds[i] * Time.deltaTime;
            rings[i].Root.transform.localEulerAngles = new Vector3(0f, 0f, zRots[i]);
        }
    }
}
