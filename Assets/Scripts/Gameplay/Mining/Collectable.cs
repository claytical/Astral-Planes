using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
public class Collectable : MonoBehaviour
{
    public InstrumentTrack assignedInstrumentTrack; // 🎼 The track that spawned this collectable
    public SpriteRenderer energySprite;
    public int burstId; 
    private int amount = 1;
    private int noteDurationTicks = 4; // 🎵 Default to 1/16th note duration
    private int assignedNote;          // 🎵 The MIDI note value
    public Transform ribbonMarker;           // assigned when spawned
    public NoteTether tether;               // runtime
    private bool awaitingPulse = false;
    public int intendedStep = -1;       // set at spawn (authoritative target)

    private bool isInitialized = false;
    private bool reachedDestination = false;

    // Idempotency flags
    private bool _handled;            // prevents double processing on trigger
    private bool _destroyNotified;    // prevents double OnDestroyed

    // Optional availability tension (currently only used if you enable it)
    private double _availableUntilDsp;
    [SerializeField] [Range(0.6f, 1.4f)]
    private float availabilityFactor = 1.0f;
    [SerializeField] private float pulseSpeed = 1.6f;
    [SerializeField] private float minAlpha = 0.25f;
    [SerializeField] private float maxAlpha = 0.65f;
    [SerializeField] private float pulseScale = 1.08f;
    private Coroutine _pulseRoutine;
    public delegate void OnCollectedHandler(int duration, float force);
    public event OnCollectedHandler OnCollected;   // informational; does not call the track
    public event Action OnDestroyed;               // for bookkeeping (track cleans lists, etc.)

    public List<int> sharedTargetSteps = new List<int>();
    // --- Movement (grid-aware, dust-adjacent) ---
    [Header("Movement")]
    [SerializeField] private float minSpeed = 0.8f;     // world units/sec for longest notes
    [SerializeField] private float maxSpeed = 3.5f;     // world units/sec for shortest notes
    [SerializeField] private float arriveRadius = 0.05f;
    [SerializeField] private float chooseNextEvery = 0.15f; // seconds between neighbor picks while en route
    [SerializeField] private float lingerLongNote = 0.25f;  // extra pause for long notes at each node
    [SerializeField] private float lingerShortNote = 0.02f; // minimal pause for short notes
    [SerializeField] private float dustAdjacencyProbe = 0.42f; // radius used to detect nearby dust
    [SerializeField] private int   neighborRadiusCells = 1;    // how far from current cell to consider neighbors (4-neighbors)
    private Rigidbody2D _rb;
    private Vector3 _targetWorld;     // current node center to walk to
    private bool _hasTarget;
    private float _speed;
    private System.Random _rng;       // deterministic per-track/per-note

    // Small helper: normalize duration ticks (large => slow)
    private float Duration01()
    {
        // Heuristic: clamp to [1..16] 16ths; you can wire drumTrack.totalSteps if you prefer.
        float t = Mathf.Clamp(noteDurationTicks, 1, 16);
        // map: 1  -> 0 (short/fast)   16 -> 1 (long/slow)
        return (t - 1f) / 15f;
    }

    private float ComputeMoveSpeed()
    {
        // Invert so long notes are slow: v = lerp(maxSpeed -> minSpeed, duration01)
        float d = Duration01();
        return Mathf.Lerp(maxSpeed, minSpeed, d);
    }

    private float ComputeLingerSeconds()
    {
        // More linger for long notes
        float d = Duration01();
        return Mathf.Lerp(lingerShortNote, lingerLongNote, d);
    }

    // stable seed so different InstrumentTracks exhibit slightly different “turn biases”
    private int StableSeed()
    {
        unchecked
        {
            int a = assignedInstrumentTrack ? assignedInstrumentTrack.GetHashCode() : 17;
            int b = assignedNote;
            int c = noteDurationTicks;
            return (a * 486187739) ^ (b * 1009) ^ (c * 9176);
        }
    }

    private bool IsPositionInsideDust(Vector2 worldPos) {
                // Robust: if ClosestPoint == query point, we are inside that collider.
        var hits = Physics2D.OverlapCircleAll(worldPos, dustAdjacencyProbe); 
        for (int i = 0; i < hits.Length; i++) {
            var dust = hits[i] ? hits[i].GetComponent<CosmicDust>() : null; 
            if (!dust) continue; 
            Vector2 cp = hits[i].ClosestPoint(worldPos); 
            if ((cp - worldPos).sqrMagnitude < 1e-6f) return true;
        } 
        return false;
    }

    private bool IsAdjacentToDust(Vector2 worldPos) { 
        // Near an edge but not inside: distance-to-edge within ring.
        var hits = Physics2D.OverlapCircleAll(worldPos, dustAdjacencyProbe * 1.35f); 
        for (int i = 0; i < hits.Length; i++) { 
            var dust = hits[i] ? hits[i].GetComponent<CosmicDust>() : null; 
            if (!dust) continue; 
            Vector2 cp = hits[i].ClosestPoint(worldPos); 
            float d = (cp - worldPos).magnitude; 
            if (d > 0.02f && d <= dustAdjacencyProbe) return true; // edge ring
        }
        return false;
    }

    private IEnumerable<Vector2Int> FourNeighbors(Vector2Int g) {
        yield return new Vector2Int(g.x + 1, g.y);
        yield return new Vector2Int(g.x - 1, g.y);
        yield return new Vector2Int(g.x, g.y + 1);
        yield return new Vector2Int(g.x, g.y - 1);
    }

    private bool TryPickNextGridNode(out Vector3 worldCenter) {
        worldCenter = transform.position;
        var dt = assignedInstrumentTrack ? assignedInstrumentTrack.drumTrack : null;
        if (dt == null) return false;

        Vector2Int here = dt.WorldToGridPosition(transform.position);

        // Collect walkable candidates that are not inside dust but are adjacent to dust.
        var candidates = new List<Vector3>(8);
        foreach (var n in FourNeighbors(here))
        {
            Vector3 c = dt.GridToWorldPosition(n);
            if (IsPositionInsideDust(c)) continue;      // avoid moving *through* dust
            if (!IsAdjacentToDust(c)) continue;         // hug the dust edges
            candidates.Add(c);
        }

        // Fallback: if hugging produced no options, allow any 4-neighbor that isn’t inside dust.
        if (candidates.Count == 0)
        {
            foreach (var n in FourNeighbors(here))
            {
                Vector3 c = dt.GridToWorldPosition(n);
                if (!IsPositionInsideDust(c))
                    candidates.Add(c);
            }
        }

        if (candidates.Count == 0) return false;

        // Deterministic “turn bias”: prefer keeping roughly the same heading
        // but break ties via seeded RNG to vary between tracks/notes.
        _rng ??= new System.Random(StableSeed());
        // score by (prefer farther from current target to avoid jitter, then random jitter)
        Vector3 pos = transform.position;
        float bestScore = float.NegativeInfinity;
        Vector3 best = candidates[0];
       for (int i = 0; i < candidates.Count; i++)
        {
            float dist = Vector3.SqrMagnitude(candidates[i] - pos);
            float jitter = (float)_rng.NextDouble() * 0.2f;
            float score = dist + jitter;
            if (score > bestScore) { bestScore = score; best = candidates[i]; }
        }

        worldCenter = best;
        return true;
    }

    private IEnumerator MovementRoutine()
    { 
        var dt = assignedInstrumentTrack ? assignedInstrumentTrack.drumTrack : null; 
        if (!_rb && !TryGetComponent(out _rb)) yield break; 
        _speed  = ComputeMoveSpeed(); 
        float linger = ComputeLingerSeconds();
        // Pick a NEIGHBOR immediately; don't snap/linger on current cell
        _hasTarget = TryPickNextGridNode(out _targetWorld); 
        float nextChooseAt = 0f;        
        while (true) {
            yield return new WaitForFixedUpdate();
            if (!_hasTarget)
            {
                if (!TryPickNextGridNode(out _targetWorld))
                {
                    //wait for next physics tick
                    continue;
                }
                _hasTarget = true;
            }

            // Move toward current node center
            Vector2 cur = _rb.position;
            Vector2 tgt = _targetWorld;
            Vector2 delta = tgt - cur;
            float dist = delta.magnitude;

            if (dist <= arriveRadius)
            {
               // perch/linger depends on note length
               float t = 0f; 
               while (t < linger) { t += Time.deltaTime; yield return null; }
               _hasTarget = false;
                continue;
            }

            Vector2 step = delta.normalized * _speed * Time.fixedDeltaTime;
            _rb.MovePosition(cur + step);
            // Periodically allow re-evaluation of neighbors to keep motion lively
            if (Time.time >= nextChooseAt)
            {
                nextChooseAt = Time.time + chooseNextEvery;
                // Occasionally re-pick mid-run for scurry feel on short notes
                float d01 = Duration01();
                bool allowMidcourse = _rng.NextDouble() < Mathf.Lerp(0.35f, 0.05f, d01); // short notes more midcourse changes
                if (allowMidcourse && TryPickNextGridNode(out var mid))
                {
                    _targetWorld = mid;
                }
            }
        }
    }
    public int GetNote() => assignedNote;
    public bool IsDark { get; private set; } = false;
    
    public void Initialize(int note, int duration, InstrumentTrack track, NoteSet noteSet, List<int> steps)
    {
        assignedNote              = note;
        noteDurationTicks         = duration;
        assignedInstrumentTrack   = track;

        // 🔒 defensive copy — do NOT keep caller's mutable list
        sharedTargetSteps = (steps != null && steps.Count > 0)
            ? new List<int>(steps)
            : new List<int>();

        if (sharedTargetSteps.Count == 0)
            Debug.LogWarning($"{gameObject.name} - No target steps provided.");

        if (TryGetComponent(out CollectableParticles particleScript) && noteSet != null)
            particleScript.ConfigureByDuration(noteSet, duration, track);

        if (track != null)
            energySprite.color = track.trackColor;

        if (energySprite != null && track != null)
        {
            var c = track.trackColor;
            c.a = Mathf.Clamp01(maxAlpha);
            energySprite.color = c;

            if (_pulseRoutine != null) StopCoroutine(_pulseRoutine);
            _pulseRoutine = StartCoroutine(PulseEnergySprite());
        }

        if (assignedInstrumentTrack == null)
            Debug.LogError($"Collectable {gameObject.name} - assignedInstrumentTrack is NULL on initialization!");

        StartCoroutine(DarkTimeoutRoutine(track));
        if (_rb == null) TryGetComponent(out _rb);
        _rng ??= new System.Random(StableSeed());
        StartCoroutine(MovementRoutine());
    }
    public void AttachTetherAtSpawn(Transform marker, GameObject tetherPrefabGO, Color trackColor, int durationTicksOrSteps, int anchorStep) {
        var viz = GameFlowManager.Instance ? GameFlowManager.Instance.noteViz : null;
        if (!assignedInstrumentTrack) {
            Debug.LogWarning("[Collectable] AttachTetherAtSpawn aborted: assignedInstrumentTrack is null.");
            return;
        }
        if (!viz) {
            Debug.LogWarning("[Collectable] AttachTetherAtSpawn aborted: NoteVisualizer is null.");
            return;
        }
        if (!tetherPrefabGO) {
            Debug.LogWarning("[Collectable] AttachTetherAtSpawn aborted: tether prefab is null.");
            return;
        }
        ribbonMarker = marker;
        Debug.Log($"[Collectable] AttachTetherAtSpawn track={assignedInstrumentTrack.name} " +
              $"baseStep={intendedStep} anchorStep={anchorStep} " +
              $"marker={(marker ? marker.name : "(null)")}");
    // --- Resolve anchorStep to an ABSOLUTE track step for expanded tracks ---
    int resolvedStep = anchorStep;
    try
    {
        int binSize  = Mathf.Max(1, assignedInstrumentTrack.BinSize());
        int total    = Mathf.Max(binSize, assignedInstrumentTrack.GetTotalSteps());
        int mult     = Mathf.Max(1, total / binSize); 
        int local    = ((anchorStep % binSize) + binSize) % binSize; // normalize 0..binSize-1
        // Prefer an intendedStep if it matches the same bin-local index.
        if (intendedStep >= 0 && (intendedStep % binSize) == local && intendedStep < total)
        {
            resolvedStep = intendedStep;
        }
        else
        {
            // Otherwise pick the first bin that actually has a marker for (track, absStep)
            bool found = false;
            for (int b = 0; b < mult; b++)
            {
                int abs = b * binSize + local;
                if (abs < 0 || abs >= total) continue;
                if (viz.noteMarkers != null &&
                    viz.noteMarkers.TryGetValue((assignedInstrumentTrack, abs), out var tr) && tr) {
                    resolvedStep = abs;
                    found = true;
                    break;
                }
            }
            if (!found) {
                // Last resort: if anchorStep already lies in absolute range, keep it; else map to bin 0
                if (anchorStep < 0 || anchorStep >= total) resolvedStep = local; // bin 0
                Debug.LogWarning($"[Collectable] Anchor rebind fallback: track={assignedInstrumentTrack.name} " +
                                 $"anchorStep={anchorStep}→{resolvedStep} (no existing marker found for expanded bins).");
            }
        } 
    } 
    catch (System.Exception ex)    { 
        Debug.LogWarning($"[Collectable] Anchor step resolution failed; using anchorStep={anchorStep}. {ex.Message}");
            resolvedStep = anchorStep;     
    } 
    if (tether != null)
    {
        // already attached; refresh binding
        tether.SetEndpoints(transform, ribbonMarker, trackColor, 1f);
        tether.BindByStep(assignedInstrumentTrack, resolvedStep, viz);
        return;
    }

    var go = Instantiate(tetherPrefabGO);
    go.name = $"Tether_{assignedInstrumentTrack.name}_s{resolvedStep}";
    tether = go.GetComponent<NoteTether>();
    if (!tether) tether = go.AddComponent<NoteTether>();

    // Set endpoints immediately (even if marker is temporarily unresolved)
    tether.SetEndpoints(transform, ribbonMarker, trackColor, 1f);

    // 🔑 Provide (track, step, viz) so the tether can re-find the marker if it’s not ready yet
    tether.BindByStep(assignedInstrumentTrack, resolvedStep, viz);

    // Optional: keep world space (don’t parent under UI/canvas)
    // go.transform.SetParent(null, worldPositionStays: true);

    // Particle drip hookup if present
    if (TryGetComponent(out CollectableParticles cp) && tether != null)
        cp.RegisterTether(tether, pull: 0.7f);
}
    public void TravelAlongTetherAndFinalize(int durationTicks, float force, float seconds = 0.35f)
    {
        StartCoroutine(TravelRoutine(durationTicks, force, seconds));
    }
    void Start()
    {
        isInitialized = true;
    }
    private IEnumerator DarkTimeoutRoutine(InstrumentTrack track)
    {
        var drums = track != null ? track.drumTrack : null;
        if (drums == null) yield break;

        // ~just over one loop; tweak to taste or make profile-driven
        float ttl = Mathf.Max(3f, drums.GetLoopLengthInSeconds() * 1.1f);
        yield return new WaitForSeconds(ttl);

        // Become "dark" (muted look), but keep collider & collection live
        IsDark = true;

        if (energySprite != null)
        {
            var c = energySprite.color;
            // desaturate & lower alpha a bit
            var grey = new Color(0.25f, 0.25f, 0.25f, Mathf.Clamp01(c.a * 0.55f));
            energySprite.color = grey;
        }

        // If we have a marker, ensure it’s greyed
        if (ribbonMarker != null)
        {
            var ml = ribbonMarker.GetComponent<MarkerLight>() ?? ribbonMarker.gameObject.AddComponent<MarkerLight>();
            ml.SetGrey(new Color(1f, 1f, 1f, 0.18f));
        }
    }
    private IEnumerator TravelRoutine(int durationTicks, float force, float seconds)
    {
        if (TryGetComponent(out Collider2D col)) col.enabled = false;
        var ps = GetComponentInChildren<ParticleSystem>();
        if (ps) ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);

        float t = 0f;
        while (t < seconds && tether)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / seconds);
            transform.position = tether.EvaluatePosition01(u);
            yield return null;
        }
        if (ribbonMarker) transform.position = ribbonMarker.position;

        var ml = ribbonMarker ? ribbonMarker.GetComponent<MarkerLight>() : null;
        if (ml) ml.LightUp(tether != null ? tether.baseColor : Color.white);

        OnCollected?.Invoke(durationTicks, force); // ✅ event raised inside Collectable
        OnDestroyed?.Invoke();

        if (tether) Destroy(tether.gameObject);
        Destroy(gameObject);
    }
    private IEnumerator PulseEnergySprite()
    {
        Vector3 startScale = transform.localScale;
        while (true)
        {
            float t = (Mathf.Sin(Time.time * pulseSpeed) * 0.5f) + 0.5f; // 0..1
            float a = Mathf.Lerp(minAlpha, maxAlpha, t);
            if (energySprite != null)
            {
                var col = energySprite.color; col.a = a; energySprite.color = col;
            }
            transform.localScale = startScale * Mathf.Lerp(1f, pulseScale, t);
            yield return null;
        }
    }
    private void NotifyDestroyedOnce()
    {
        if (_destroyNotified) return;
        _destroyNotified = true;
        OnDestroyed?.Invoke();
    }
    private void OnEnable()
    {
        _handled = false;
        _destroyNotified = false;
    }
    private void OnDestroy()  { NotifyDestroyedOnce(); }
    private void OnDisable()  { NotifyDestroyedOnce(); } // pooling-safe
    private void OnTriggerEnter2D(Collider2D coll)
    {
        var vehicle = coll.GetComponent<Vehicle>();
        if (vehicle == null || _handled) return;
        _handled = true; // ✅ idempotent

        // (Optional availability check)
        // if (_availableUntilDsp > 0 && AudioSettings.dspTime > _availableUntilDsp) { OnFailedCollect(); return; }

        // 1) compute loop timing
        var drumTrack    = assignedInstrumentTrack.drumTrack;
        double dspNow    = AudioSettings.dspTime;
        double loopLen   = drumTrack.GetLoopLengthInSeconds();
        double stepDur   = loopLen / drumTrack.totalSteps;
        double timingWin = stepDur * drumTrack.timingWindowSteps * 0.5;
        double loopStart = drumTrack.startDspTime;
        double tPos      = (dspNow - loopStart) % loopLen;
        if (tPos < 0) tPos += loopLen;

        // 2) pick the best matching target step (within timing window)
        int matchedStep = -1;
        double bestErr = timingWin;
        for (int i = 0; i < sharedTargetSteps.Count; i++)
        {
            int step = sharedTargetSteps[i];
            double stepPos = (step * stepDur) % loopLen;
            double delta   = Math.Abs(stepPos - tPos);
            if (delta > loopLen * 0.5) delta = loopLen - delta;

            if (delta < bestErr)
            {
                bestErr = delta;
                matchedStep = step;
            }
        }

            // success path
            float force = vehicle.GetForceAsMidiVelocity();
            vehicle.CollectEnergy(amount);
            Debug.Log("[COLLECT] Collectable Triggered");
            assignedInstrumentTrack.OnCollectableCollected(this, intendedStep >= 0 ? intendedStep : matchedStep, noteDurationTicks, force);
            if (TryGetComponent(out Collider2D col)) col.enabled = false;
            var explode = GetComponent<Explode>();
            if(explode != null) explode.Permanent(false);

    }
}
