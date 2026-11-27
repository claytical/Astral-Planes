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
    private int _targetPhaseIndex = -1;
    private int _targetMotifIndex = -1;

    // Carry-over: an uncaught wisp whose exit was taken is parked here until the new motif's
    // maze/entry finishes, then released to wander until that motif's first loop boundary.
    private bool _carried;
    private bool _parked;
    private DrumTrack _loopDrum;
    public static Wisp PendingCarry { get; private set; }
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

    // Bridge samples still playing (they outlive their wisp on purpose). Faded out at motif
    // completion so a long sample never bleeds into the next motif's maze and wisp.
    private static readonly System.Collections.Generic.List<AudioSource> s_activeBridgeSources = new();

    /// <summary>Fades every still-playing wisp bridge sample to silence over
    /// <paramref name="seconds"/>, then stops and destroys it. Safe to call repeatedly.</summary>
    public static void FadeOutActiveBridgeSamples(float seconds)
    {
        for (int i = s_activeBridgeSources.Count - 1; i >= 0; i--)
        {
            var src = s_activeBridgeSources[i];
            s_activeBridgeSources.RemoveAt(i);
            if (src == null) continue;
            if (!src.gameObject.TryGetComponent<BridgeSampleFader>(out var fader))
                fader = src.gameObject.AddComponent<BridgeSampleFader>();
            fader.Begin(src, seconds);
        }
    }

    // Returns false if the wisp couldn't be placed (missing config, or no path from the
    // vehicle to the exit) — callers must destroy the GameObject rather than leave it
    // sitting whichever cell Instantiate happened to use, which historically defaulted to
    // the vehicle's own cell and caused an instant, silent self-consume on spawn.
    public bool Initialize(DrumTrack drum, Vector2Int vehicleStartCell, Vector2Int targetExitCell, MotifProfile targetMotif,
                           int targetPhaseIndex = -1, int targetMotifIndex = -1,
                           Vector2Int? preferredSpawnCell = null)
    {
        _drum = drum;
        _targetPhaseIndex = targetPhaseIndex;
        _targetMotifIndex = targetMotifIndex;
        _targetCell = targetExitCell;
        _targetMotif = targetMotif;
        PreloadBridgeSamples();

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

        // Preferred spawn (where the finished record vanished): nearest open cell to it, as long
        // as it can still reach the exit. Falls back to the route band below otherwise.
        if (preferredSpawnCell.HasValue &&
            TryFindNearestOpenCell(drum, preferredSpawnCell.Value, out var openCell) &&
            _pathfinder.TryFindPath(drum, openCell, targetExitCell))
        {
            Vector2Int cell = openCell;
            // Don't appear inside the ship's trigger (instant self-consume) — step a little
            // along the route toward the exit instead.
            if (Mathf.Max(Mathf.Abs(cell.x - vehicleStartCell.x), Mathf.Abs(cell.y - vehicleStartCell.y)) <= 1)
            {
                var wp = _pathfinder.Waypoints;
                cell = wp[Mathf.Min(2, wp.Count - 1)];
            }

            PlaceAt(drum, cell);
            Debug.Log($"[Wisp] spawned at record vanish cell {cell} (record cell {preferredSpawnCell.Value})");
            return true;
        }

        if (!_pathfinder.TryFindPath(drum, vehicleStartCell, targetExitCell))
            return false; // re-run: the preferred-cell attempt above may have overwritten the route

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

            // Bias toward the ships' end of the route so the wisp is seen leading through most of
            // the maze, not appearing right beside its exit. Intersected with the margins; if the
            // fraction band misses them entirely the margins alone apply.
            float fLo = Mathf.Clamp01(Mathf.Min(config.spawnPathFraction.x, config.spawnPathFraction.y));
            float fHi = Mathf.Clamp01(Mathf.Max(config.spawnPathFraction.x, config.spawnPathFraction.y));
            int bandMin = Mathf.Max(minIndex, Mathf.FloorToInt((count - 1) * fLo));
            int bandMax = Mathf.Min(maxIndex, Mathf.CeilToInt((count - 1) * fHi));
            if (bandMin <= bandMax)
            {
                minIndex = bandMin;
                maxIndex = bandMax;
            }
            else
            {
                maxIndex = minIndex; // band sits below the vehicle margin: closest allowed cell
            }
        }

        int spawnIndex = Random.Range(minIndex, maxIndex + 1);
        PlaceAt(drum, waypoints[spawnIndex]);
        if (preferredSpawnCell.HasValue)
            Debug.Log($"[Wisp] record vanish cell {preferredSpawnCell.Value} unusable — fell back to route band");
        return true;
    }

    private void PlaceAt(DrumTrack drum, Vector2Int cell)
    {
        _rb.position = drum.GridToWorldPosition(cell);
        transform.position = _rb.position;

        _wanderSeedX = Random.Range(0f, 1000f);
        _wanderSeedY = Random.Range(0f, 1000f);
    }

    // Ring-by-ring outward search for the closest dust-free cell (the record's own cell may now
    // sit inside a maze wall).
    private static bool TryFindNearestOpenCell(DrumTrack drum, Vector2Int start, out Vector2Int cell)
    {
        const int kMaxRadius = 8;
        int w = drum.GetSpawnGridWidth();
        int h = drum.GetSpawnGridHeight();
        cell = start;
        if (w <= 0 || h <= 0) return false;
        start.x = Mathf.Clamp(start.x, 0, w - 1);
        start.y = Mathf.Clamp(start.y, 0, h - 1);

        for (int r = 0; r <= kMaxRadius; r++)
        {
            bool found = false;
            float bestSq = float.MaxValue;
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != r) continue; // ring only
                    var c = new Vector2Int(start.x + dx, start.y + dy);
                    if (c.x < 0 || c.y < 0 || c.x >= w || c.y >= h) continue;
                    if (drum.HasDustAt(c)) continue;
                    float sq = dx * dx + dy * dy;
                    if (sq < bestSq) { bestSq = sq; cell = c; found = true; }
                }
            if (found) return true;
        }
        return false;
    }

    private void FixedUpdate()
    {
        if (_consumed || _parked || _drum == null || config == null) return;

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
        if (_carried) return; // target exit no longer exists; just wander until the loop ends

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

    public bool IsAssignedTo(int phaseIndex, int motifIndex) =>
        !_consumed && _targetPhaseIndex == phaseIndex && _targetMotifIndex == motifIndex;

    // Uncaught and the player left through a different exit: never seen again.
    public void Dismiss()
    {
        if (_consumed) return;
        _consumed = true;
        if (PendingCarry == this) PendingCarry = null;
        Destroy(gameObject);
    }

    // Hide and park until the next motif is up (see BeginCarryPhase).
    public void CarryOverToNextMotif()
    {
        if (_consumed) return;
        if (PendingCarry != null && PendingCarry != this) PendingCarry.Dismiss();
        _parked = true;
        _committed = false;
        _wandering = false;
        _rb.linearVelocity = Vector2.zero;
        SetVisible(false);
        PendingCarry = this;
    }

    // Called after the chosen motif's maze is up and vehicles are in. Reappears near the entry
    // edge, wanders, and despawns at the motif's first loop boundary.
    public static void BeginPendingCarry(DrumTrack drum, BoundaryWrap.BoundarySide entryEdge, Vector2Int entryCell)
    {
        var w = PendingCarry;
        PendingCarry = null;
        if (w == null) return;
        if (drum == null) { w.Dismiss(); return; }
        w.BeginCarryPhase(drum, entryEdge, entryCell);
    }

    private void BeginCarryPhase(DrumTrack drum, BoundaryWrap.BoundarySide entryEdge, Vector2Int entryCell)
    {
        _drum = drum;
        _carried = true;
        _parked = false;
        _wandering = true;

        // Reappear deep in the new maze (an open cell carrySpawnCellRange BFS steps from the entry)
        // so it isn't sitting at the entry road. Fallback: a few cells in from the entry edge.
        Vector2 pos;
        var range = config != null ? config.carrySpawnCellRange : new Vector2Int(10, 18);
        if (_pathfinder.TryFindCellAtDistance(drum, entryCell, Mathf.Min(range.x, range.y), Mathf.Max(range.x, range.y), out var deepCell))
        {
            pos = drum.GridToWorldPosition(deepCell);
        }
        else
        {
            Vector2 outward = SceneFlowCoordinator.OutwardDirection(entryEdge);
            pos = (Vector2)drum.GridToWorldPosition(entryCell) - outward * (drum.GetCellWorldSize() * 3f);
        }
        _rb.position = pos;
        transform.position = pos;
        SetVisible(true);

        _loopDrum = drum;
        _loopDrum.OnLoopBoundary += OnCarriedLoopBoundary;
    }

    private void OnCarriedLoopBoundary()
    {
        if (_consumed) return;
        _consumed = true;
        if (_explode != null) _explode.Permanent(); else Destroy(gameObject);
    }

    private void OnDestroy()
    {
        if (_loopDrum != null) _loopDrum.OnLoopBoundary -= OnCarriedLoopBoundary;
        if (PendingCarry == this) PendingCarry = null;
    }

    private void SetVisible(bool visible)
    {
        foreach (var r in GetComponentsInChildren<Renderer>(true)) r.enabled = visible;
        foreach (var c in GetComponentsInChildren<Collider2D>(true)) c.enabled = visible;
        foreach (var ps in GetComponentsInChildren<ParticleSystem>(true))
        {
            if (visible) ps.Play(); else ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (_consumed || _parked) return;
        var vehicle = other.GetComponentInParent<Vehicle>();
        if (vehicle == null) return;
        Consume(vehicle);
    }

    private void Consume(Vehicle vehicle)
    {
        _consumed = true;
        if (_loopDrum != null) _loopDrum.OnLoopBoundary -= OnCarriedLoopBoundary;
        vehicle.RecoverEnergy(vehicle.capacity);
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

        // Deliberately unparented (and scene-load proof): this must keep playing across the motif
        // bridge, well past this wisp's own destruction and the interstitial maze's cleanup.
        var go = new GameObject("WispBridgeSample");
        DontDestroyOnLoad(go);
        var source = go.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = false;
        source.spatialBlend = 0f;
        source.clip = clip;
        source.volume = _targetMotif.bridgeSampleVolume;

        // The bridge clips import with Preload Audio Data off. Scheduling a clip whose data isn't
        // resident is the suspected silent path, so only quantize when it's loaded; otherwise
        // kick the load and play immediately (Play() loads synchronously when needed).
        var loadState = clip.loadState;
        if (loadState == AudioDataLoadState.Unloaded) clip.LoadAudioData();
        bool loaded = clip.loadState == AudioDataLoadState.Loaded;

        double delay = 0.0;
        bool scheduled = false;
        if (loaded && _targetMotif.quantizeBridgeSampleToBeat && _drum != null &&
            _drum.TryGetNextBaseStepDsp(out double dspTime, out float stepSec, stepOffset: 1))
        {
            delay = dspTime - AudioSettings.dspTime;
            // A stale/future DSP anchor mid-bridge could push the start far out (or past this
            // object's lifetime); only quantize when it's within a couple of steps.
            if (delay >= 0.0 && delay <= Mathf.Max(0.05f, stepSec * 2f))
            {
                source.PlayScheduled(dspTime);
                scheduled = true;
            }
            else delay = 0.0;
        }
        if (!scheduled) source.Play();

        Debug.Log($"[Wisp] bridge sample '{clip.name}' from motif '{_targetMotif.motifId}' vol={source.volume:F3} " +
                  $"loadState={loadState}->{clip.loadState} {(scheduled ? $"scheduled +{delay:F3}s" : "immediate")}");

        Destroy(go, (float)delay + clip.length + 0.25f);
        s_activeBridgeSources.RemoveAll(s => s == null);
        s_activeBridgeSources.Add(source);
    }

    // Bridge clips import with Preload Audio Data off; load them now so the data is resident by
    // the time a vehicle reaches the wisp.
    private void PreloadBridgeSamples()
    {
        if (_targetMotif == null || _targetMotif.bridgeSamples == null) return;
        foreach (var clip in _targetMotif.bridgeSamples)
            if (clip != null && clip.loadState == AudioDataLoadState.Unloaded)
                clip.LoadAudioData();
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
