using UnityEngine;

// Shared base for the two independently-tuned "wandering node" actors:
// DiscoveryTrackNode (carver/shard) and SuperNodeTrackNode (bonus-round chaser).
// Only plumbing confirmed identical in shape between both lives here — locomotion,
// direction-scoring formulas, and escape-direction logic stay subclass-owned.
public abstract class TrackNode : MonoBehaviour
{
    protected static readonly Vector2[] EightDirections =
    {
        Vector2.right,
        new Vector2( 1f,  1f).normalized,
        Vector2.up,
        new Vector2(-1f,  1f).normalized,
        Vector2.left,
        new Vector2(-1f, -1f).normalized,
        Vector2.down,
        new Vector2( 1f, -1f).normalized,
    };

    // ---------------------------------------------------------------
    // Loop-boundary expiry
    // ---------------------------------------------------------------
    protected int _loopsSinceSpawn;
    protected int _expireAfterLoops;
    private DrumTrack _loopBoundarySource;

    protected void SubscribeLoopBoundary(DrumTrack drumTrack)
    {
        _loopBoundarySource = drumTrack;
        if (_loopBoundarySource != null)
            _loopBoundarySource.OnLoopBoundary += HandleLoopBoundary;
    }

    protected void UnsubscribeLoopBoundary()
    {
        if (_loopBoundarySource != null)
            _loopBoundarySource.OnLoopBoundary -= HandleLoopBoundary;
    }

    protected virtual void HandleLoopBoundary()
    {
        if (IsResolvedOrHandled) return;
        _loopsSinceSpawn++;
        if (_expireAfterLoops > 0 && _loopsSinceSpawn >= _expireAfterLoops)
            Expire();
    }

    // True once the subclass has already committed to a terminal outcome
    // (captured/collected) and loop-boundary expiry should stop applying.
    protected abstract bool IsResolvedOrHandled { get; }
    protected abstract void Expire();

    // ---------------------------------------------------------------
    // Stall-timer sampling — periodic low-movement detection only.
    // What to do about a confirmed stall (escape direction, cooldown)
    // stays fully subclass-owned.
    // ---------------------------------------------------------------
    protected int _stallHits;
    private Vector2 _stallSamplePos;
    private float _nextStallSampleAt;

    protected bool TrySampleStall(Vector2 currentPos, float sampleInterval, float distanceThreshold,
                                   int requiredHits, bool extraGate = true)
    {
        bool confirmedStall = false;
        if (Time.time >= _nextStallSampleAt)
        {
            float moved = (currentPos - _stallSamplePos).magnitude;
            if (_nextStallSampleAt > 0f)
            {
                if (moved < distanceThreshold && extraGate) _stallHits++;
                else _stallHits = Mathf.Max(0, _stallHits - 1);
                confirmedStall = (_stallHits >= requiredHits);
            }
            _stallSamplePos = currentPos;
            _nextStallSampleAt = Time.time + sampleInterval;
        }
        return confirmedStall;
    }

    protected void ResetStallSample()
    {
        _stallHits = 0;
    }

    // ---------------------------------------------------------------
    // Resolve-once guard. Public OnResolved event shape stays per-subclass.
    // ---------------------------------------------------------------
    private bool _resolvedFired;
    protected bool ResolvedFired => _resolvedFired;

    protected bool TryMarkResolved()
    {
        if (_resolvedFired) return false;
        _resolvedFired = true;
        return true;
    }
}
