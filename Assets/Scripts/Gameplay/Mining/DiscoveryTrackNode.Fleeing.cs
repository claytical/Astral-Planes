using UnityEngine;

public partial class DiscoveryTrackNode
{
    private void FixedUpdateFleeing()
    {
        if (_hasPrevPhysicsPos) EnforceGridContainment(_prevPhysicsPos, _rb.position);

        Vector2Int myCell = _drumTrack.WorldToGridPosition(_rb.position);
        RunCorridorLookahead(myCell);

        int stepNow     = _drumTrack.currentStep;
        int currentNote = _noteSet.GetNoteForPhaseAndRole(_track, stepNow);
        float speed01   = Mathf.InverseLerp(_track.lowestAllowedNote, _track.highestAllowedNote, currentNote);

        bool gapOnNearSide = false;
        if (_stunTimer > 0f)
        {
            // Stunned: dash away on the heading locked at hit-time. No exit-seeking —
            // gapOnNearSide stays false, so the boundary clamp below stays fully closed.
            _stunTimer -= Time.fixedDeltaTime;
            float hitStunSpeedMultiplier = _activeLocomotionProfile != null ? _activeLocomotionProfile.hitStunSpeedMultiplier : kDefaultHitStunSpeedMultiplier;
            ApplyLocomotion(speed01, hitStunSpeedMultiplier);
        }
        else
        {
            ApplyLocomotion(speed01, 1f);

            // Seek the nearest reachable side-wall gap and steer toward it. Escape is
            // only ever through a dust-free perimeter cell on the LEFT or RIGHT column.
            _fleeGapFinder.Update(_drumTrack, myCell, Time.fixedDeltaTime);

            if (_fleeGapFinder.HasGap)
            {
                Vector2Int gapCell = _fleeGapFinder.GapCell;
                Vector2 waypoint = _drumTrack.GridToWorldPosition(_fleeGapFinder.WaypointCell);
                Vector2 toGap    = waypoint - _rb.position;
                if (toGap.sqrMagnitude > 0.0001f)
                {
                    float gapBias = _activeLocomotionProfile != null ? _activeLocomotionProfile.fleeCommitment01 : kDefaultFleeCommitment01;
                    // At the doorway, override corridor lookahead so it can't turn the node away.
                    int cellDist = Mathf.Abs(myCell.x - gapCell.x) + Mathf.Abs(myCell.y - gapCell.y);
                    if (cellDist < 3) gapBias = Mathf.Max(gapBias, 0.9f);
                    _carveDir = Vector2.Lerp(_carveDir, toGap.normalized, gapBias).normalized;
                }

                // Open the X clamp only when the node is at its gap: gap wall is the node's
                // near wall, node is within one row of the gap, and the perimeter cell in the
                // node's own row is authoritative — dust there = wall, no exit.
                int w = Mathf.Max(1, _drumTrack.GetSpawnGridWidth());
                int edgeX = gapCell.x > 0 ? w - 1 : 0;
                bool gapWallIsNear = (gapCell.x == 0) == (myCell.x <= (w - 1) / 2);
                gapOnNearSide = gapWallIsNear
                             && Mathf.Abs(myCell.y - gapCell.y) <= 1
                             && !_drumTrack.HasDustAt(new Vector2Int(edgeX, myCell.y));
            }
        }

        RunStallEscape(myCell);
        if (!ShouldSkipBoundaryClampThisTick())
            RunBoundaryClamp(!gapOnNearSide, true); // X clamp opens only at the sought gap

        // Off-screen escape: LEFT and RIGHT only, and only through a genuine wall gap.
        // Top and bottom off-screen must never call HandleEscape().
        Vector2 posNow = _rb.position;
        if (_drumTrack.TryGetPlayAreaWorld(out var area))
        {
            float margin = Mathf.Max(0.5f, _drumTrack.GetCellWorldSize()); // ~1 cell past the grid edge
            bool outside = posNow.x < area.left  - margin
                        || posNow.x > area.right + margin;
            if (!outside) return;

            if (ExitGapCrossed(posNow))
            {
                HandleEscape();
                return;
            }
            // Slipped past a solid wall (containment miss): shove back inside instead of escaping.
            if (!didContainmentThisTick)
                ClampIntoPlayArea(posNow, area);
        }
        else
        {
            // Play area unavailable: fall back to camera geometry (no gap check possible).
            if (_cam == null) _cam = Camera.main;
            if (_cam != null && CanEscapeFromWorldPos(posNow, _cam.orthographicSize * _cam.aspect, 2f))
            {
                HandleEscape();
                return;
            }
        }
    }

    /// True when the border cell the node crossed (WorldToGridPosition clamps off-grid
    /// positions back onto the perimeter) — or a neighbor along that wall — is dust-free.
    /// The one-cell tolerance forgives drift across a 2-cell gap while still requiring
    /// adjacency to a real opening; neighbors are checked along the wall only, never inward.
    private bool ExitGapCrossed(Vector2 pos)
    {
        Vector2Int b = _drumTrack.WorldToGridPosition(pos);
        if (!_drumTrack.HasDustAt(b)) return true;

        // Side walls only (escape is never through top or bottom) → vary y along the wall.
        int h = Mathf.Max(1, _drumTrack.GetSpawnGridHeight());
        return !_drumTrack.HasDustAt(new Vector2Int(b.x, Mathf.Min(b.y + 1, h - 1)))
            || !_drumTrack.HasDustAt(new Vector2Int(b.x, Mathf.Max(b.y - 1, 0)));
    }

    private void ClampIntoPlayArea(Vector2 pos, DrumTrack.PlayArea area)
    {
        Vector2 clamped = pos;
        clamped.x = Mathf.Clamp(clamped.x, area.left + 0.1f, area.right - 0.1f);
        clamped.y = Mathf.Min(clamped.y, area.top - 0.1f);
        if ((clamped - pos).sqrMagnitude < 0.0001f) return;

        _rb.position = clamped;
        Vector2 inward = (clamped - pos).normalized;
        _carveDir = Vector2.Lerp(_carveDir, inward, 0.75f).normalized;
        _rb.linearVelocity = _carveDir * _rb.linearVelocity.magnitude;
        didContainmentThisTick = true;
        _hardCorrectionsThisTick++;
    }

    private static bool CanEscapeFromWorldPos(Vector2 pos, float halfW, float margin)
    {
        // Keep this rule in sync with boundary-trigger behavior:
        // escape permitted from left/right only; never from top or bottom.
        return pos.x < -halfW - margin
            || pos.x > halfW + margin;
    }
}
