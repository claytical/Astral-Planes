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

    // ---------------------------------------------------------------
    // Direction scan — shared "pick best of 8, jitter, set timers" control
    // flow. Scoring formulas, reaction-delay/commit-duration sources, and
    // jitter magnitude stay subclass-owned: DiscoveryTrackNode scores via
    // DrumTrack grid lookups + decision-archetype tuning, SuperNodeTrackNode
    // scores via Physics2D raycasts + fixed serialized floats. The gating
    // logic deciding *when* to rescan (wall-ahead detection, rescan timers,
    // behavior-intent broadcasting) also stays subclass-owned — the two
    // systems detect "wall ahead" differently and only one broadcasts intent.
    // ---------------------------------------------------------------
    protected Vector2 _carveDir = Vector2.right;
    protected float _nextDirectionDecisionAt;
    protected float _pathCommitUntil;

    protected abstract float ScoreDirection(Vector2 pos, Vector2 dir);
    protected abstract float TurnJitterDegrees();
    protected abstract float NextReactionDelay();
    protected abstract float NextPathCommitDuration();

    protected void RunDirectionScan(Vector2 pos)
    {
        float bestScore = float.MinValue;
        Vector2 bestDir = _carveDir;

        foreach (var dir in EightDirections)
        {
            float score = ScoreDirection(pos, dir);
            if (score > bestScore) { bestScore = score; bestDir = dir; }
        }

        float jitter = Random.Range(-TurnJitterDegrees(), TurnJitterDegrees());
        _carveDir = Rotate(bestDir.normalized, jitter).normalized;

        _nextDirectionDecisionAt = Time.time + NextReactionDelay();
        _pathCommitUntil = Time.time + NextPathCommitDuration();
    }

    protected static Vector2 Rotate(Vector2 v, float degrees)
    {
        float r = degrees * Mathf.Deg2Rad;
        float c = Mathf.Cos(r);
        float s = Mathf.Sin(r);
        return new Vector2(c * v.x - s * v.y, s * v.x + c * v.y);
    }
}
