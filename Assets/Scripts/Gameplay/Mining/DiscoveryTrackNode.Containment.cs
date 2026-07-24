using UnityEngine;

public partial class DiscoveryTrackNode
{
    private void EnforceGridContainment(Vector2 fromPos, Vector2 toPos)
    {
        var hit = GridSweepContainmentUtility.FindFirstBlockedCrossing(_drumTrack, fromPos, toPos);
        if (!hit.hit) return;

        Vector2 wallNormal = hit.normal.sqrMagnitude > 0.0001f ? hit.normal.normalized : Vector2.zero;
        Vector2 target = hit.impactPoint - wallNormal * 0.02f;
        _rb.position = target;
        didContainmentThisTick = true;
        _hardCorrectionsThisTick++;

        float inwardSpeed = Vector2.Dot(_rb.linearVelocity, -wallNormal);
        if (inwardSpeed > 0f)
            _rb.linearVelocity += wallNormal * inwardSpeed;

        if (_carveDir.sqrMagnitude > 0.001f)
        {
            Vector2 tangent = new Vector2(-wallNormal.y, wallNormal.x);
            _carveDir = Vector2.Lerp(_carveDir, tangent * Mathf.Sign(Vector2.Dot(_carveDir, tangent)), 0.6f).normalized;
        }

        if (debugSweepContainment)
            if (GameFlowManager.VerboseLogging) Debug.Log($"[DiscoveryTrackNode] blockedCell={hit.blockedCell} normal={wallNormal} target={target}");
    }

    private bool ShouldSkipBoundaryClampThisTick()
    {
        // When swept dust containment already performed a hard correction this tick,
        // skip the subsequent play-area hard clamp to preserve single-correction ownership.
        return didContainmentThisTick;
    }

    private void RunBoundaryClamp(bool clampX, bool clampY)
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return;

        const float pad = 0.5f;
        Vector2 pos  = _rb.position;
        Vector2 minB = _cam.ViewportToWorldPoint(new Vector3(0f, 0f, 0f));
        Vector2 maxB = _cam.ViewportToWorldPoint(new Vector3(1f, 1f, 0f));
        bool hit = false;

        if (clampX)
        {
            if      (pos.x < minB.x + pad) { pos.x = minB.x + pad; _carveDir.x =  Mathf.Abs(_carveDir.x); hit = true; }
            else if (pos.x > maxB.x - pad) { pos.x = maxB.x - pad; _carveDir.x = -Mathf.Abs(_carveDir.x); hit = true; }
        }
        if (clampY)
        {
            if      (pos.y < minB.y + pad) { pos.y = minB.y + pad; _carveDir.y =  Mathf.Abs(_carveDir.y); hit = true; }
            else if (pos.y > maxB.y - pad) { pos.y = maxB.y - pad; _carveDir.y = -Mathf.Abs(_carveDir.y); hit = true; }
        }

        if (hit)
        {
            _rb.position = pos;
            _rb.linearVelocity = _carveDir * _rb.linearVelocity.magnitude;
            didContainmentThisTick = true;
            _hardCorrectionsThisTick++;
            if (assertSingleHardCorrectionPerTick && _hardCorrectionsThisTick > 1)
                Debug.LogAssertion($"[DiscoveryTrackNode] Multiple hard corrections in one tick on {name}: {_hardCorrectionsThisTick}", this);
        }
    }
}
