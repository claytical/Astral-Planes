using UnityEngine;

public partial class DiscoveryTrackNode
{
    private void FixedUpdateDrifting()
    {
        if (_hasPrevPhysicsPos) EnforceGridContainment(_prevPhysicsPos, _rb.position);

        Vector2Int myCell = _drumTrack.WorldToGridPosition(_rb.position);

        if (_behaviorCategory == DiscoveryTrackNodeBehaviorCategory.Orbital)
        {
            UpdateOrbitSign(myCell);
            CheckOrbitTerritoryFlip(myCell);
        }

        float burstMult = 1f;
        if (_behaviorCategory == DiscoveryTrackNodeBehaviorCategory.Rhythmic)
        {
            UpdateBurstPause();
            burstMult = _isInBurst ? 2f : 0.05f;
        }

        // Skip direction decisions during Groove pause — node is nearly stationary
        if (_behaviorCategory != DiscoveryTrackNodeBehaviorCategory.Rhythmic || _isInBurst)
            RunCorridorLookahead(myCell);

        ApplyLocomotion(0f, burstMult);

        // Groove pause: actively damp velocity; kMinSpeedFloor inside ApplyLocomotion prevents a true stop otherwise
        if (_behaviorCategory == DiscoveryTrackNodeBehaviorCategory.Rhythmic && !_isInBurst)
            _rb.linearVelocity = Vector2.Lerp(_rb.linearVelocity, Vector2.zero, 0.25f);

        RunStallEscape(myCell);
        if (!ShouldSkipBoundaryClampThisTick())
            RunBoundaryClamp(true, true);
    }

    private void UpdateOrbitSign(Vector2Int myCell)
    {
        if (_stallHits == 0) { _orbitSignLocked = false; return; }
        if (_orbitSignLocked) return;
        _orbitSign = -_orbitSign;
        _orbitSignLocked = true;
    }

    private void CheckOrbitTerritoryFlip(Vector2Int myCell)
    {
        if (_dustGenerator == null || _orbitSignLocked) return;
        if (_dustGenerator.GetZoneRole(myCell) != _role)
        {
            _orbitSign = -_orbitSign;
            _orbitSignLocked = true;
        }
    }

    private void UpdateBurstPause()
    {
        if (_burstPauseSnapPending)
        {
            if (Time.time >= _burstPauseSnapAt)
            {
                _isInBurst = !_isInBurst;
                _burstPauseSnapPending = false;
                _burstPauseTimer = _isInBurst
                    ? (_roleProfile?.burstDuration  ?? 0.4f)
                    : (_roleProfile?.pauseDuration  ?? 0.35f);
            }
            return;
        }
        _burstPauseTimer -= Time.fixedDeltaTime;
        if (_burstPauseTimer > 0f) return;

        // Schedule transition at next DSP beat-step boundary
        if (_drumTrack != null && _drumTrack.leaderStartDspTime > 0)
        {
            double dspNow     = AudioSettings.dspTime;
            double loopLen    = _drumTrack.GetClipLengthInSeconds();
            double stepDur    = _stepsPerLoop > 0 ? loopLen / _stepsPerLoop : 0.1;
            double timeInStep = (dspNow - _drumTrack.leaderStartDspTime) % stepDur;
            _burstPauseSnapAt = Time.time + (float)(stepDur - timeInStep);
        }
        else
        {
            _burstPauseSnapAt = Time.time;
        }
        _burstPauseSnapPending = true;
    }
}
