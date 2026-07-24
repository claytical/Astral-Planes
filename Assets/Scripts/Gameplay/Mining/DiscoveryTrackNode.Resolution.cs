using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public partial class DiscoveryTrackNode
{
    // Star-drained dust cells whose energy built this node; they regrow when it dies.
    private List<Vector2Int> _heldDustCells;
    private bool _heldDustReleased;

    private void HandleDepleted(Vehicle vehicle)
    {
        _depletedHandled = true;
        WasCaptured = true;

        var origin    = transform.position;
        var repelFrom = vehicle != null ? vehicle.transform.position : origin;

        Vector2 blastDir = ((Vector2)(origin - repelFrom)).sqrMagnitude > 0.0001f
            ? ((Vector2)(origin - repelFrom)).normalized
            : Vector2.up;

        var explode = GetComponent<Explode>();
        if (explode != null) explode.SetBurstDirection(blastDir);

        _track.SpawnCollectableBurst(_noteSet, -1, -1, origin, repelFrom, 1.0f, 140f, 0.18f, InstrumentTrack.BurstPlacementMode.Free, 10);
        TriggerExplosion();
    }

    private void TryPlayPreviewNote()
    {
        if (_track == null || _noteSet == null || _drumTrack == null) return;
        int stepNow = _drumTrack.currentStep;
        int note    = _noteSet.GetNoteForPhaseAndRole(_track, stepNow);
        _track.PlayNote127(note, 200, 0.5f);
        if (GetComponent<Explode>() != null) GetComponent<Explode>().PreExplode();
    }

    private void SetBehaviorIntent(DiscoveryTrackNodeBehaviorIntent intent)
    {
        if (_behaviorIntent == intent) return;
        _behaviorIntent = intent;
        OnBehaviorIntentChanged?.Invoke(intent);
    }

    private IEnumerator CleanupAndDestroy(bool waitForFullEscape = false)
    {
        if (waitForFullEscape)
            yield return StartCoroutine(WaitUntilFullyOutsidePlayArea());
        else
            yield return null;
        var dt = _drumTrack;
        if (dt != null)
        {
            UnsubscribeLoopBoundary();
            _drumTrack.FreeSpawnCell(_spawnCell.x, _spawnCell.y);
            dt.UnregisterMineNode(this);
        }
        Destroy(gameObject);
    }

    private IEnumerator WaitUntilFullyOutsidePlayArea()
    {
        if (_rb == null)
        {
            yield return null;
            yield break;
        }

        float timeoutAt = Time.time + 6f;
        while (Time.time < timeoutAt)
        {
            if (IsFullyOutsidePlayArea(_rb.position))
                yield break;

            yield return new WaitForFixedUpdate();
        }
    }

    private bool IsFullyOutsidePlayArea(Vector2 worldPos)
    {
        // Disabled colliders report empty bounds, so use the radius cached at escape time.
        float radius = _escapeGlideRadius;
        if (_col != null && _col.enabled)
            radius = Mathf.Max(_col.bounds.extents.x, _col.bounds.extents.y);

        // Use the visible camera frame first so escape cleanup waits until the node is fully off-screen,
        // not merely outside DrumTrack's playable sub-rect.
        if (_cam == null) _cam = Camera.main;
        if (_cam != null)
        {
            float halfH = _cam.orthographicSize;
            float halfW = halfH * _cam.aspect;
            Vector2 camPos = _cam.transform.position;

            bool outsideX = worldPos.x < camPos.x - halfW - radius || worldPos.x > camPos.x + halfW + radius;
            bool outsideY = worldPos.y < camPos.y - halfH - radius || worldPos.y > camPos.y + halfH + radius;
            return outsideX || outsideY;
        }

        if (_drumTrack == null || !_drumTrack.TryGetPlayAreaWorld(out var area))
            return true;

        bool outsidePlayAreaX = worldPos.x < area.left - radius || worldPos.x > area.right + radius;
        bool outsidePlayAreaY = worldPos.y < area.bottom - radius || worldPos.y > area.top + radius;
        return outsidePlayAreaX || outsidePlayAreaY;
    }

    private void TriggerExplosion()
    {
        if (GameFlowManager.VerboseLogging) Debug.Log($"Triggering Explosion in Mine Node");
        var explosion = GetComponent<Explode>();
        if (explosion != null) explosion.Permanent(false);
        FireResolvedOnce();
        StartCoroutine(CleanupAndDestroy());
    }

    private void FireResolvedOnce()
    {
        if (!TryMarkResolved()) return;
        ReleaseHeldDustOnce();
        var outcome = WasCaptured ? DiscoveryTrackNodeOutcome.Captured
                    : WasEscaped  ? DiscoveryTrackNodeOutcome.Escaped
                                  : DiscoveryTrackNodeOutcome.Expired;
        OnResolved?.Invoke(this, outcome);
    }

    public void AttachHeldDustBatch(List<Vector2Int> cells)
    {
        if (cells == null || cells.Count == 0) return;
        _heldDustCells ??= new List<Vector2Int>();
        _heldDustCells.AddRange(cells);
    }

    private void ReleaseHeldDustOnce()
    {
        if (_heldDustReleased) return;
        _heldDustReleased = true;
        if (_heldDustCells == null || _heldDustCells.Count == 0) return;
        GameFlowManager.Instance?.dustGenerator?.ReleaseHeldCells(_heldDustCells);
        _heldDustCells.Clear();
    }

    private IEnumerator ScaleSmoothly(Vector3 targetScale, float duration)
    {
        Vector3 initialScale = transform.localScale;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            transform.localScale = Vector3.Lerp(initialScale, targetScale, elapsed / duration);
            elapsed += Time.deltaTime;
            yield return null;
        }
        transform.localScale = targetScale;
        if (transform.localScale.magnitude <= 0.05f) TriggerExplosion();
    }

    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (_depletedHandled) return;
        if (!coll.gameObject.TryGetComponent<Vehicle>(out var vehicle)) return;

        TryPlayPreviewNote();

        Vector2 awayDir = (Vector2)transform.position - (Vector2)vehicle.transform.position;
        if (awayDir.sqrMagnitude < 0.0001f) awayDir = _carveDir;
        _carveDir = awayDir.normalized;

        _loopsSinceSpawn = 0;
        _stunTimer = config.hitStunDuration;
        // Lock RunCorridorLookahead's commit window so it doesn't immediately overwrite the dash heading.
        _nextDirectionDecisionAt = Time.time + config.hitStunDuration;
        _pathCommitUntil        = Time.time + config.hitStunDuration;

        _strength -= vehicle.GetForceAsDamage();
        _strength  = Mathf.Max(0, _strength);

        TransitionToFleeing();

        float normalized  = (_maxStrength > 0) ? (float)_strength / _maxStrength : 0f;
        float scaleFactor = Mathf.Lerp(0.3f, 1.1f, normalized);
        StartCoroutine(ScaleSmoothly(_originalScale * scaleFactor, 0.1f));

        if (_strength <= 0) HandleDepleted(vehicle);
    }
}
