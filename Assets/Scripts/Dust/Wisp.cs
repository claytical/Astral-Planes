using UnityEngine;
using Random = UnityEngine.Random;

// Spawned once per multi-exit path-choice interstitial maze (see
// PathChoiceCoordinator.RunPathChoiceAndAdvance). Idles until a vehicle closes to
// approachRadius, then commits permanently to leading the way toward its assigned exit's
// AnchorCell. Touching it plays a one-shot "bridging" sample meant to carry across the
// motif transition — independent of which exit actually wins the path choice.
[RequireComponent(typeof(Rigidbody2D))]
public class Wisp : MonoBehaviour
{
    [SerializeField] private WispConfig config;
    [SerializeField] private Explode _explode;

    private DrumTrack _drum;
    private Rigidbody2D _rb;
    private readonly WispExitPathfinder _pathfinder = new();
    private Vector2Int _targetCell;
    private MotifProfile _targetMotif;
    private int _waypointIndex;
    private bool _committed;
    private bool _consumed;
    private bool _wandering;
    private int _pathFailureStreak;
    private float _nextCheckAt;

    private float _driftPhase;
    private float _driftHz;

    private float _wanderSeedX;
    private float _wanderSeedY;
    private float _wanderPhase;

    private static readonly Collider2D[] _overlapBuffer = new Collider2D[8];

    // Returns false if the wisp couldn't be placed (missing config, or no path from the
    // vehicle to the exit) — callers must destroy the GameObject rather than leave it
    // sitting whichever cell Instantiate happened to use, which historically defaulted to
    // the vehicle's own cell and caused an instant, silent self-consume on spawn.
    public bool Initialize(DrumTrack drum, Vector2Int vehicleStartCell, Vector2Int targetExitCell, MotifProfile targetMotif)
    {
        _drum = drum;
        _targetCell = targetExitCell;
        _targetMotif = targetMotif;

        _rb = GetComponent<Rigidbody2D>();
        _rb.gravityScale = 0f;
        _rb.constraints = RigidbodyConstraints2D.FreezeRotation;

        if (_explode == null) _explode = GetComponent<Explode>();

        if (config == null)
        {
            Debug.LogWarning("[Wisp] No WispConfig assigned on the wisp prefab — skipping spawn.");
            return false;
        }

        if (!_pathfinder.TryFindPath(drum, vehicleStartCell, targetExitCell))
        {
            Debug.LogWarning($"[Wisp] No path found from vehicle cell {vehicleStartCell} to exit anchor {targetExitCell} — skipping spawn.");
            return false;
        }

        var waypoints = _pathfinder.Waypoints;
        int count = waypoints.Count;

        // Each margin is computed from its own end of the corridor, independently, then
        // intersected — clamping either bound against the other before the intersection
        // (e.g. capping minIndex at count-1) would let one margin silently swallow the
        // other on a short corridor. Only fall back to the midpoint if the two margins
        // leave no valid band at all.
        int minIndex = config.spawnMinCellsFromVehicle;
        int maxIndex = (count - 1) - config.spawnMinCellsFromExit;

        if (minIndex > maxIndex)
        {
            minIndex = maxIndex = Mathf.Clamp(count / 2, 0, count - 1);
        }
        else
        {
            minIndex = Mathf.Clamp(minIndex, 0, count - 1);
            maxIndex = Mathf.Clamp(maxIndex, 0, count - 1);
        }

        int spawnIndex = Random.Range(minIndex, maxIndex + 1);
        Vector2Int spawnCell = waypoints[spawnIndex];

        _rb.position = drum.GridToWorldPosition(spawnCell);
        transform.position = _rb.position;

        _wanderSeedX = Random.Range(0f, 1000f);
        _wanderSeedY = Random.Range(0f, 1000f);
        return true;
    }

    private void FixedUpdate()
    {
        if (_consumed || _drum == null || config == null) return;

        if (_committed)
        {
            AdvanceAlongPath();
            return;
        }

        // Not committed: either fully at rest waiting for the vehicle (a real path to
        // the assigned exit still exists), or wandering because it doesn't (see
        // TryCommit — most commonly hit if this wisp survived into a new motif whose
        // dust grid no longer has its original exit).
        if (_wandering)
            UpdateWander();

        if (Time.time >= _nextCheckAt)
        {
            _nextCheckAt = Time.time + config.recheckPeriod;
            TryCommit();
        }
    }

    private void TryCommit()
    {
        // Path viability is checked regardless of vehicle distance — otherwise a wisp
        // whose target has become unreachable would just sit frozen forever waiting for
        // an approach check that, even once it succeeds, would still fail to commit.
        Vector2Int myCell = _drum.WorldToGridPosition(_rb.position);
        bool hasPath = _pathfinder.TryFindPath(_drum, myCell, _targetCell);

        if (!hasPath)
        {
            // Require several consecutive failures (not just one) before concluding the
            // target is genuinely gone — a single transient BFS miss (e.g. right after
            // spawn, or a regrow tick mid-transition) must not immediately send the wisp
            // wandering off with zero chance for the vehicle to ever have approached.
            // The real "survived into a new motif" case fails consistently and will
            // clear this threshold easily; a one-off blip won't.
            _pathFailureStreak++;
            if (_pathFailureStreak >= config.wanderGiveUpFailures)
                _wandering = true;
            return;
        }

        _pathFailureStreak = 0;
        _wandering = false;
        if (!HasVehicleWithin(_rb.position, config.approachRadius)) return;

        _waypointIndex = 0;
        // Randomized once per commit so multiple wisps (across a session) don't weave in lockstep.
        _driftHz = Random.Range(config.travelDriftHzRange.x, config.travelDriftHzRange.y);
        _driftPhase = 0f;
        _committed = true;
    }

    // Pure "wander steering": the heading itself is driven by continuously-varying
    // Perlin noise (not a fixed anchor point to hover around), so it reads as genuine
    // ambient drifting through the dust field. Bounded to the play area so it can't
    // wander off-screen indefinitely.
    private void UpdateWander()
    {
        _wanderPhase += Time.fixedDeltaTime * config.wanderNoiseFrequency;
        float nx = Mathf.PerlinNoise(_wanderSeedX, _wanderPhase) - 0.5f;
        float ny = Mathf.PerlinNoise(_wanderSeedY, _wanderPhase) - 0.5f;

        Vector2 heading = new Vector2(nx, ny);
        if (heading.sqrMagnitude > 0.0001f) heading.Normalize();

        SteerToward(heading * (config.seekSpeed * config.wanderSpeedFraction));

        if (_drum.TryGetPlayAreaWorld(out var area))
            SuperNodeSteeringMath.ClampToBounds(_rb, Rect.MinMaxRect(area.left, area.bottom, area.right, area.top));
    }

    private void AdvanceAlongPath()
    {
        var waypoints = _pathfinder.Waypoints;
        if (waypoints == null || _waypointIndex >= waypoints.Count)
        {
            SteerToward(Vector2.zero);
            return;
        }

        Vector2 targetWorld = _drum.GridToWorldPosition(waypoints[_waypointIndex]);
        Vector2 toTarget = targetWorld - _rb.position;

        if (toTarget.magnitude <= config.waypointArriveDistance)
        {
            _waypointIndex++;
            if (_waypointIndex >= waypoints.Count)
            {
                SteerToward(Vector2.zero);
                return;
            }
            targetWorld = _drum.GridToWorldPosition(waypoints[_waypointIndex]);
            toTarget = targetWorld - _rb.position;
        }

        // Sine-modulated lateral weave (same idea as the Lead role's serpentine movement
        // in Collectable.Movement.cs) layered onto the straight-line pursuit direction,
        // so the wisp reads as drifting toward the exit rather than snapping to it.
        Vector2 forward = toTarget.normalized;
        Vector2 perp = new Vector2(-forward.y, forward.x);
        _driftPhase += _driftHz * Mathf.PI * 2f * Time.fixedDeltaTime;
        float weave = Mathf.Sin(_driftPhase) * config.travelDriftAmplitude;

        SteerToward(forward * config.seekSpeed + perp * weave);
    }

    // Acceleration-limited steering (Vector2.MoveTowards, not a hard velocity snap) —
    // same approach Collectable.Movement.cs uses — so turns lag softly instead of
    // instantly re-pointing every frame.
    private void SteerToward(Vector2 desiredVelocity)
    {
        _rb.linearVelocity = Vector2.MoveTowards(_rb.linearVelocity, desiredVelocity, config.steerAcceleration * Time.fixedDeltaTime);
    }

    private bool HasVehicleWithin(Vector2 pos, float radius)
    {
        int count = Physics2D.OverlapCircle(pos, radius, ContactFilter2D.noFilter, _overlapBuffer);
        for (int i = 0; i < count; i++)
        {
            if (_overlapBuffer[i] != null && _overlapBuffer[i].GetComponentInParent<Vehicle>() != null)
                return true;
        }
        return false;
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (_consumed) return;
        if (other.GetComponentInParent<Vehicle>() == null) return;
        Consume();
    }

    private void Consume()
    {
        _consumed = true;
        _rb.linearVelocity = Vector2.zero;
        PlayBridgeSample();
        BeginMazePulse();

        // Explode.Permanent() destroys the GameObject itself once its burst is queued —
        // see SuperNodeTrackNode.Resolve() for the same pattern. Only fall back to a
        // direct Destroy when no Explode is wired up.
        _explode?.Permanent();
        if (_explode == null) Destroy(gameObject);
    }

    private void PlayBridgeSample()
    {
        if (_targetMotif == null)
        {
            Debug.LogWarning("[Wisp] No target MotifProfile resolved for this wisp — nothing to play on contact.");
            return;
        }

        if (_targetMotif.bridgeSamples == null || _targetMotif.bridgeSamples.Count == 0)
        {
            Debug.LogWarning($"[Wisp] Target motif '{_targetMotif.motifId}' has no bridgeSamples assigned — nothing to play on contact.");
            return;
        }

        var clip = _targetMotif.bridgeSamples[Random.Range(0, _targetMotif.bridgeSamples.Count)];
        if (clip == null)
        {
            Debug.LogWarning($"[Wisp] Picked a null entry from '{_targetMotif.motifId}'.bridgeSamples — nothing to play on contact.");
            return;
        }

        // Deliberately unparented: this must keep playing across the motif bridge, well
        // past this wisp's own destruction and the interstitial maze's cleanup.
        var go = new GameObject("WispBridgeSample");
        var source = go.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = false;
        source.spatialBlend = 0f;
        source.clip = clip;
        source.volume = _targetMotif.bridgeSampleVolume;

        if (_targetMotif.quantizeBridgeSampleToBeat && _drum != null &&
            _drum.TryGetNextBaseStepDsp(out double dspTime, out _, stepOffset: 1))
            source.PlayScheduled(dspTime);
        else
            source.Play();

        Destroy(go, clip.length + 0.25f);
    }

    private void BeginMazePulse()
    {
        if (config == null) return;

        var dustGen = GameFlowManager.Instance?.dustGenerator;
        if (dustGen == null) return;

        dustGen.BeginRainbowPulseAllDust(
            config.mazePulseSeconds, config.mazePulseHueCyclesPerSecond,
            config.mazePulseSaturation, config.mazePulseValue);
    }
}
