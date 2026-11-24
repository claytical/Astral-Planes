using System.Collections;
using UnityEngine;

// ── Record dock (record recovery) ───────────────────────────────────────
// The record lives in the field for the whole motif:
//  • Motif start (PlaceDocked): the record is placed at its dock spot before its placeholders
//    draw in, so it never scales in or out; the camera zooms instead (GameFlowManager.
//    IntroduceRecord). Rings recorded during play grow on the docked record; the stack keeps its
//    outer edge at the dock radius (FitDocked). DockRecord (animated move) is only the fallback.
//  • Motif complete (DockForCollection): placeholders drop, the last ring finishes deforming, and
//    the record becomes collectable. If it never docked at motif start, it is shown full-size,
//    then shrinks and moves to a dock spot here instead.
// While docked: no auto-hide, no play-area refit. Ship contact breaks/plucks tethers as before
// during play, and collects the record once RecordReady (Vehicle.Damage.cs). The spin-off exit
// plays from the dock at dock size (SpinAndRollOffActiveRings restores _fitScale).
public partial class MotifRingGlyphApplicator
{
    private bool    _docked;
    private bool    _dockMoving;
    private Vector3 _dockWorldPos;
    private float   _dockWorldRadius;

    /// <summary>True from docking until the record's exit animation finishes (or the stack is cleared).</summary>
    public bool IsDocked => _docked;

    public bool HasGameplayRings => _gameplayRings.Count > 0;

    // Things that keep the stack visible and in place: an imminent spin-off, or a docked record.
    private bool HoldVisible => _spinOffPending || _docked;

    // Local radius of the stack's outer edge for ringCount rings (same formula as RefreshPlayAreaFit).
    private float StackOuterLocal(int ringCount) =>
        ringCount > 0 ? RingInnerRadius(ringCount - 1) + config.ringThickness : 0f;

    private Vector3 DockScaleFor(float outerLocal)
    {
        float s = outerLocal > 0f ? _dockWorldRadius / outerLocal : 1f;
        return new Vector3(s, s, 1f);
    }

    // Called from RefreshPlayAreaFit while docked: keep the outer edge at the dock radius.
    private void FitDocked(int ringCount, float? outerOverride)
    {
        if (_dockMoving || config == null) return; // the move animation tracks the target itself
        float outer = outerOverride ?? StackOuterLocal(ringCount);
        if (outer <= 0f) return;
        transform.position   = new Vector3(_dockWorldPos.x, _dockWorldPos.y, transform.position.z);
        transform.localScale = DockScaleFor(outer);
        _fitScale            = transform.localScale;
    }

    /// <summary>Ring deformations still animating (notes landing on a just-recorded ring).</summary>
    public int PendingDeformationCount => _pendingDeformationCount;

    /// <summary>World position of the docked record (valid while <see cref="IsDocked"/>).</summary>
    public Vector3 DockWorldPosition => _dockWorldPos;

    /// <summary>
    /// Docks the record at <paramref name="worldPos"/> immediately, with no scale or move animation:
    /// call before ShowInitialRecord so the intro placeholders draw in at dock size. The camera
    /// (CameraZoomDirector) does the zooming instead.
    /// </summary>
    public void PlaceDocked(Vector3 worldPos, float worldRadius)
    {
        _docked          = true;
        _dockMoving      = false;
        _dockWorldPos    = worldPos;
        _dockWorldRadius = Mathf.Max(0.01f, worldRadius);
        if (_gameplayHideCoroutine != null)
        {
            StopCoroutine(_gameplayHideCoroutine);
            _gameplayHideCoroutine = null;
        }
        transform.position = new Vector3(worldPos.x, worldPos.y, transform.position.z);
        FitDocked(_gameplayRings.Count + _remainingRings.Count, null);
    }

    /// <summary>
    /// Shrinks and moves the current stack (normally the intro placeholders) so its outer edge sits
    /// at <paramref name="worldRadius"/> around <paramref name="worldPos"/>, and keeps it there.
    /// </summary>
    public IEnumerator DockRecord(Vector3 worldPos, float worldRadius)
    {
        if (config == null) yield break;

        _docked          = true;
        _dockMoving      = true;
        _dockWorldPos    = worldPos;
        _dockWorldRadius = Mathf.Max(0.01f, worldRadius);
        if (_gameplayHideCoroutine != null)
        {
            StopCoroutine(_gameplayHideCoroutine);
            _gameplayHideCoroutine = null;
        }
        if (_ringCollider != null) _ringCollider.enabled = false; // nothing solid while it flies

        Vector3 startPos   = transform.position;
        Vector3 endPos     = new Vector3(worldPos.x, worldPos.y, startPos.z);
        Vector3 startScale = transform.localScale;
        float   dur        = Mathf.Max(0.01f, config.dockMoveDuration);
        for (float t = 0f; t < dur; t += Time.deltaTime)
        {
            float k = Mathf.SmoothStep(0f, 1f, t / dur);
            // Ring count can change mid-flight (a placeholder rebuild), so re-read the target.
            var target = DockScaleFor(StackOuterLocal(_gameplayRings.Count + _remainingRings.Count));
            transform.position   = Vector3.Lerp(startPos, endPos, k);
            transform.localScale = Vector3.Lerp(startScale, target, k);
            yield return null;
        }
        _dockMoving = false;
        FitDocked(_gameplayRings.Count + _remainingRings.Count, null);
        EnableRingColliderAndPhase(); // no-op until the first gameplay ring exists
    }

    /// <summary>
    /// Motif complete: readies the record for collection. Already docked → drop placeholders and
    /// let the last ring land. Not docked → show it full-size, then shrink/move it to the dock spot.
    /// Run nested by the caller.
    /// </summary>
    public IEnumerator DockForCollection(Vector3 worldPos, float worldRadius)
    {
        if (config == null || _gameplayRings.Count == 0) yield break;

        if (_docked)
        {
            SetRemainingProgressRings(0); // refits to the dock radius (FitDocked)
            yield return WaitForFinalDeformation();
            EnableRingColliderAndPhase();
            yield break;
        }

        // Fallback (no dock at motif start): reveal the finished record, then fly it to the dock.
        // SetRemainingProgressRings refits (snapping scale to the full fit), so keep the current
        // scale and let the reveal animate instead.
        Vector3 preScale = transform.localScale;
        SetRemainingProgressRings(0);
        transform.localScale = preScale;

        if (_gameplayHideCoroutine != null)
        {
            StopCoroutine(_gameplayHideCoroutine);
            _gameplayHideCoroutine = null;
        }
        if (_ringCollider != null) _ringCollider.enabled = false;
        _spinOffPending = true; // hold visible through the reveal (cleared by DockRecord's _docked)

        Vector3 fullScale = _fitScale.sqrMagnitude > 0.0001f ? _fitScale : Vector3.one;
        float showDur = Mathf.Max(0.01f, config.scaleDownDuration);
        Vector3 from = transform.localScale;
        for (float t = 0f; t < showDur; t += Time.deltaTime)
        {
            transform.localScale = Vector3.Lerp(from, fullScale, Mathf.SmoothStep(0f, 1f, t / showDur));
            yield return null;
        }
        transform.localScale = fullScale;

        yield return WaitForFinalDeformation();
        if (config.dockHoldDuration > 0f)
            yield return new WaitForSeconds(config.dockHoldDuration);

        _spinOffPending = false;
        yield return DockRecord(worldPos, worldRadius);
        EnableRingColliderAndPhase();
    }

    // Notes land in time with playback, so the final ring can take up to one loop to finish.
    private IEnumerator WaitForFinalDeformation()
    {
        var drum = GameFlowManager.Instance != null ? GameFlowManager.Instance.activeDrumTrack : null;
        float maxWait = drum != null ? drum.GetLoopLengthInSeconds() : 2f;
        for (float waited = 0f; _pendingDeformationCount > 0 && waited < maxWait; waited += Time.deltaTime)
            yield return null;
    }
}
