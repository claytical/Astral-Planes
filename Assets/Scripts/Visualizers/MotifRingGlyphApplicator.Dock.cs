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
//  • Trapped (DockDriftRoutine): while sealed in its vault the record wanders inside its chamber and
//    every few seconds lunges at the shell (OnDockBump → GameFlowManager flashes the shell there).
//    The drift stops once the record is collectable, and during any exit. While the camera zooms
//    (CameraZoomDirector.ZoomActive: intro, ring and ready spotlights) the record glides back to the
//    chamber centre and holds still, so it sits exactly where the zoom is anchored.
// While docked: no auto-hide, no play-area refit. Ship contact breaks/plucks tethers as before
// during play, and collects the record once RecordReady (Vehicle.Damage.cs). The spin-off exit
// plays from the dock at dock size (SpinAndRollOffActiveRings restores _fitScale).
public partial class MotifRingGlyphApplicator
{
    private bool    _docked;
    private bool    _dockMoving;
    private Vector3 _dockWorldPos;
    private float   _dockWorldRadius;
    private float   _dockChamberRadius;   // world radius of the chamber the record drifts in
    private Vector3 _dockDriftOffset;
    private Coroutine _dockDriftRoutine;

    /// <summary>The trapped record hit its shell; the argument is the world direction of the hit.</summary>
    public event System.Action<Vector2> OnDockBump;

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
        transform.position   = new Vector3(_dockWorldPos.x + _dockDriftOffset.x, _dockWorldPos.y + _dockDriftOffset.y,
                                           transform.position.z);
        transform.localScale = DockScaleFor(outer);
        _fitScale            = transform.localScale;
    }

    /// <summary>Ring deformations still animating (notes landing on a just-recorded ring).</summary>
    public int PendingDeformationCount => _pendingDeformationCount;

    /// <summary>World position of the docked record (valid while <see cref="IsDocked"/>).</summary>
    public Vector3 DockWorldPosition => _dockWorldPos;

    /// <summary>World radius of the docked record's outer edge (valid while <see cref="IsDocked"/>).</summary>
    public float DockWorldRadius => _dockWorldRadius;

    /// <summary>
    /// Docks the record at <paramref name="worldPos"/> immediately, with no scale or move animation:
    /// call before ShowInitialRecord so the intro placeholders draw in at dock size. The camera
    /// (CameraZoomDirector) does the zooming instead.
    /// </summary>
    public void PlaceDocked(Vector3 worldPos, float worldRadius, float chamberRadius = 0f)
    {
        _docked            = true;
        _dockMoving        = false;
        _dockWorldPos      = worldPos;
        _dockWorldRadius   = Mathf.Max(0.01f, worldRadius);
        _dockChamberRadius = Mathf.Max(_dockWorldRadius, chamberRadius);
        _dockDriftOffset   = Vector3.zero;
        if (_gameplayHideCoroutine != null)
        {
            StopCoroutine(_gameplayHideCoroutine);
            _gameplayHideCoroutine = null;
        }
        transform.position = new Vector3(worldPos.x, worldPos.y, transform.position.z);
        FitDocked(_gameplayRings.Count + _remainingRings.Count, null);
    }

    /// <summary>
    /// Starts the trapped record's drift inside its chamber (no-op without room to move).
    /// Stopped by <see cref="StopDockDrift"/>, the exit, and ClearGameplayRings.
    /// </summary>
    public void StartDockDrift()
    {
        if (!_docked || config == null || _dockChamberRadius - _dockWorldRadius <= 0.001f) return;
        if (_dockDriftRoutine != null) StopCoroutine(_dockDriftRoutine);
        _dockDriftRoutine = StartCoroutine(DockDriftRoutine());
    }

    /// <summary>Stops the drift; with <paramref name="easeBack"/> the record glides back to the chamber centre.</summary>
    public void StopDockDrift(bool easeBack)
    {
        if (_dockDriftRoutine != null) { StopCoroutine(_dockDriftRoutine); _dockDriftRoutine = null; }
        if (easeBack && _docked && isActiveAndEnabled && _dockDriftOffset.sqrMagnitude > 0.000001f)
            _dockDriftRoutine = StartCoroutine(EaseDriftHome(0.5f));
        else
            _dockDriftOffset = Vector3.zero;
    }

    private bool DriftSuspended => _dockMoving || _gameplayFadingOut || _spinOffPending || _shakeCoroutine != null
                                   || CameraZoomDirector.ZoomActive;

    private const float kDriftSettleSeconds = 0.3f;

    // Suspended for a camera zoom: glide the record back to the chamber centre and hold it there.
    // (The other suspensions own the transform themselves, so they leave the offset alone.)
    private void SettleDriftForZoom()
    {
        if (_dockMoving || _gameplayFadingOut || _spinOffPending || _shakeCoroutine != null) return;
        if (_dockDriftOffset.sqrMagnitude < 0.000001f) { _dockDriftOffset = Vector3.zero; return; }
        float k = 1f - Mathf.Exp(-Time.deltaTime * (4.6f / kDriftSettleSeconds)); // ~99% in kDriftSettleSeconds
        _dockDriftOffset = Vector3.Lerp(_dockDriftOffset, Vector3.zero, k);
        ApplyDriftPosition();
    }

    private void ApplyDriftPosition()
    {
        transform.position = new Vector3(_dockWorldPos.x + _dockDriftOffset.x, _dockWorldPos.y + _dockDriftOffset.y,
                                         transform.position.z);
    }

    // Slow Perlin wander inside the free room, broken every few seconds by a lunge at the shell:
    // a quick push all the way to the wall (raising OnDockBump there) and a softer spring back.
    private IEnumerator DockDriftRoutine()
    {
        float seedX = Random.value * 100f, seedY = Random.value * 100f;
        float wanderT = 0f;
        float nextLunge = Time.time + Random.Range(config.dockLungeIntervalRange.x, config.dockLungeIntervalRange.y);

        while (_docked)
        {
            float room = Mathf.Max(0f, _dockChamberRadius - _dockWorldRadius);
            if (DriftSuspended)
            {
                SettleDriftForZoom();
                // Next lunge only after a calm stretch once the camera is back.
                nextLunge = Mathf.Max(nextLunge, Time.time + config.dockLungeIntervalRange.x);
                yield return null;
                continue;
            }

            wanderT += Time.deltaTime * Mathf.Max(0.01f, config.dockDriftWanderSpeed);
            Vector2 wander = new Vector2(Mathf.PerlinNoise(seedX, wanderT) - 0.5f,
                                         Mathf.PerlinNoise(seedY, wanderT) - 0.5f) * (2f * room * config.dockDriftWanderShare);

            if (Time.time >= nextLunge && room > 0.001f)
            {
                Vector2 dir   = Random.insideUnitCircle.normalized;
                if (dir.sqrMagnitude < 0.01f) dir = Vector2.right;
                Vector2 start = _dockDriftOffset;
                Vector2 hit   = dir * room;
                float   total = Mathf.Max(0.1f, config.dockLungeSeconds);
                float   push  = total * 0.3f, back = total * 0.7f;

                for (float t = 0f; t < push && _docked; t += Time.deltaTime)
                {
                    if (DriftSuspended) break;
                    float k = t / push;
                    _dockDriftOffset = Vector2.Lerp(start, hit, k * k); // accelerate into the wall
                    ApplyDriftPosition();
                    yield return null;
                }
                if (!DriftSuspended)
                {
                    _dockDriftOffset = hit;
                    ApplyDriftPosition();
                    OnDockBump?.Invoke(dir);
                }
                for (float t = 0f; t < back && _docked; t += Time.deltaTime)
                {
                    if (DriftSuspended) break;
                    _dockDriftOffset = Vector2.Lerp(hit, wander, Mathf.SmoothStep(0f, 1f, t / back));
                    ApplyDriftPosition();
                    yield return null;
                }
                nextLunge = Time.time + Random.Range(config.dockLungeIntervalRange.x, config.dockLungeIntervalRange.y);
                continue;
            }

            _dockDriftOffset = Vector2.Lerp(_dockDriftOffset, wander, 1f - Mathf.Exp(-4f * Time.deltaTime));
            ApplyDriftPosition();
            yield return null;
        }
        _dockDriftRoutine = null;
    }

    private IEnumerator EaseDriftHome(float seconds)
    {
        Vector3 from = _dockDriftOffset;
        for (float t = 0f; t < seconds; t += Time.deltaTime)
        {
            _dockDriftOffset = Vector3.Lerp(from, Vector3.zero, Mathf.SmoothStep(0f, 1f, t / seconds));
            if (!DriftSuspended) ApplyDriftPosition();
            yield return null;
        }
        _dockDriftOffset = Vector3.zero;
        if (!DriftSuspended) ApplyDriftPosition();
        _dockDriftRoutine = null;
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
            StopDockDrift(easeBack: true); // no longer trapped: settle in the middle of the chamber
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
