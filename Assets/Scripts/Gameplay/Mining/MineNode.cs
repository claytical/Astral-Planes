using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = System.Random;

public class MineNode : MonoBehaviour
{
    public event System.Action<MineNode> OnResolved;
    public SpriteRenderer coreSprite;
    public int maxStrength = 100;
    private Vector2Int _spawnCell;
    private bool _hasSpawnCell;

    private int _strength;
    private Vector3 _originalScale;
    private Color _lockedColor;
    private bool _depletedHandled, _resolvedFired;
    private Collider2D _col;
    private Rigidbody2D _rb;
    // Cached normalized pitch (0 = lowest note, 1 = highest note)
    private float _lastNote01 = 0.5f;

    [SerializeField] private float minCarveSeconds = 1.25f;
    private DrumTrack _drumTrack;

    // --- Grid-based path recording (only during carving) ---
    // Each entry is a grid cell the node occupied while carving.
    private readonly List<Vector2Int> _carvedPath = new List<Vector2Int>();
    public bool pruneCarvedPathOnLoopBoundary = true;
    [Tooltip("Minimum carved path length before pruning runs.")] 
    [Min(2)] public int pruneMinPathCount = 8;

    [Header("NoteSet-Driven Motion (Carving)")]
    [SerializeField] private bool driveCarvingMotionFromNoteSet = true;
    public float maxSteerForce = 50f;

    [SerializeField] private float carveSpeedMin = 1.25f;
    [SerializeField] private float carveSpeedMax = 8.0f;

    [SerializeField] private float steerForce = 10f;          // how hard we correct velocity toward desired dir
    [SerializeField] private float turnAngleBass = 8f;
    [SerializeField] private float turnAngleGroove = 14f;
    [SerializeField] private float turnAngleHarmony = 22f;
    [SerializeField] private float turnAngleLead = 40f;

    private NoteSet _noteSet;
    private InstrumentTrack _track;
    private MusicalRole _role;

    private int _lastProcessedStep = -1;
    private Vector2 _carveDir = Vector2.right; // will be randomized on init

    [Header("Corridor Healing (MineNode carve)")] 
    [Tooltip("Baseline seconds before carved corridor dust regrows (before age tightening).")] 
    [Min(0.1f)] public float corridorHealDelaySeconds = 6f;
    [Tooltip("Minimum seconds before regrow after the node has aged (tightening).")] 
    [Min(0.1f)] public float corridorHealMinDelaySeconds = 2f;
    [Tooltip("Over this many completed loops, corridor heal delay tightens from baseline to min.")] 
    [Min(1)] public int corridorHealTightenLoops = 4;
    private MineNodeDustInteractor _dustInteractor;
    private float _currentDesiredSpeed;


    private void Start()
    {
        _rb = GetComponent<Rigidbody2D>();
        _dustInteractor = GetComponent<MineNodeDustInteractor>();
        _originalScale = transform.localScale;
        _strength = maxStrength;
    }
private void FixedUpdate()
{
    if (!driveCarvingMotionFromNoteSet ||
        _rb == null ||
        _drumTrack == null ||
        _noteSet == null ||
        _track == null)
    {
        return;
    }

    // ---- Tunables (safe defaults) ----
    const float microTurnGate        = 0.15f; // how much we turn on non-authored steps
    const float stallSpeed           = 0.20f; // below this, consider "stalled"
    const float stuckDot             = 0.10f; // below this alignment, consider "not progressing"
    const float escapeJitterDeg      = 25f;   // random jitter after an escape redirect
    const float minDesiredSpeedFloor = 0.25f; // avoid meaningless speed caps downstream

    int stepNow = _drumTrack.currentStep;

    // --- STEP-BASED TURNING (Option A) ---
    if (stepNow != _lastProcessedStep)
    {
        _lastProcessedStep = stepNow;

        var stepList = _noteSet.GetStepList();
        bool isTurnStep = (stepList != null && stepList.Contains(stepNow));
        float turnGate = isTurnStep ? 1f : microTurnGate;

        int note = _noteSet.GetNoteForPhaseAndRole(_track, stepNow);

        float note01 = Mathf.InverseLerp(
            _track.lowestAllowedNote,
            _track.highestAllowedNote,
            note);

        _lastNote01 = note01;

        float baseAngle = GetRoleTurnAngleDeg(_role);

        // Scale turn magnitude by pitch + whether this step is authored as a turn.
        float turnAngle = Mathf.Lerp(baseAngle * 0.5f, baseAngle * 1.25f, note01) * turnGate;
        float delta = UnityEngine.Random.Range(-turnAngle, turnAngle);

        _carveDir = Rotate(_carveDir, delta).normalized;
    }

    // --- NOTE-DRIVEN SPEED ---
    int currentNote = _noteSet.GetNoteForPhaseAndRole(_track, stepNow);

    float speed01 = Mathf.InverseLerp(
        _track.lowestAllowedNote,
        _track.highestAllowedNote,
        currentNote);

    float targetSpeed = Mathf.Lerp(carveSpeedMin, carveSpeedMax, speed01);
    targetSpeed = Mathf.Max(targetSpeed, minDesiredSpeedFloor);

    if (_dustInteractor == null) _dustInteractor = GetComponent<MineNodeDustInteractor>();
    if (_dustInteractor != null) _dustInteractor.SetDesiredSpeed(targetSpeed);

    Vector2 desiredVelocity = _carveDir * targetSpeed;
    Vector2 velocityDelta = desiredVelocity - _rb.linearVelocity;

    Vector2 force = velocityDelta * steerForce * _rb.mass;
    force = Vector2.ClampMagnitude(force, maxSteerForce); // add a maxSteerForce field
    _rb.AddForce(force, ForceMode2D.Force);

    // --- BOUNDARY / STALL ESCAPE ---
    // If we're trying to move but we're not, we are likely pressed against a boundary.
    float vMag = _rb.linearVelocity.magnitude;

    float align = (vMag > 0.0001f)
        ? Vector2.Dot(_rb.linearVelocity.normalized, _carveDir)
        : 0f;

    if (vMag < stallSpeed || align < stuckDot)
    {
        // If we have some velocity, redirect away from the "wall" by reflecting across a perpendicular.
        if (vMag > 0.05f)
        {
            Vector2 n = _rb.linearVelocity.normalized;
            Vector2 approxWallNormal = new Vector2(-n.y, n.x).normalized; // perpendicular to motion
            _carveDir = Vector2.Reflect(_carveDir, approxWallNormal).normalized;
        }
        else
        {
            // If nearly stopped, force a decisive change in heading.
            _carveDir = Rotate(_carveDir, UnityEngine.Random.Range(120f, 240f)).normalized;
        }

        // Add jitter so it doesn't ping-pong.
        _carveDir = Rotate(_carveDir, UnityEngine.Random.Range(-escapeJitterDeg, escapeJitterDeg)).normalized;

        // Dampen velocity slightly so the solver can separate from the wall faster.
        _rb.linearVelocity *= 0.5f;
    }
}
public Color GetImprintShadowColor()
{
    if (_track != null)
        return _track.TrackShadowColor;

    // hard fallback
    Color c = _lockedColor;
    return new Color(c.r * 0.2f, c.g * 0.2f, c.b * 0.2f, c.a);
}


    public Color GetImprintColor()
    {
        // InstrumentTrack is the semantic source of color
        if (_track != null)
            return _track.trackColor;

        return Color.white;
    }

    public float GetImprintHardness()
    {
        // Inverted pitch:
        // low note  -> hard / long-lasting
        // high note -> soft / fast-healing
        return Mathf.Clamp01(1f - _lastNote01);
    }
    private int GetPreviewNoteForCurrentStep()
    {
        int step = (_drumTrack != null) ? _drumTrack.currentStep : 0;
        return _noteSet.GetNoteForPhaseAndRole(_track, step);
    }

    public void Initialize(InstrumentTrack track, Color tint, Vector2Int spawnCell)
    {
        _track = track;
        _spawnCell = spawnCell;
        _role          = track != null ? track.assignedRole : default;
        _noteSet = (track != null) ? GameFlowManager.Instance.GenerateNotes(track) : null;
        _lockedColor   = tint;
        // DrumTrack authority: from assigned track, not from minedObject
        _drumTrack = (track != null) ? track.drumTrack : null;

        // NoteSet authority: MineNode owns it (or you can store it on track, but currently you’re generating here)
        
        float a = UnityEngine.Random.Range(0f, 360f);
        _carveDir = new Vector2(Mathf.Cos(a * Mathf.Deg2Rad), Mathf.Sin(a * Mathf.Deg2Rad)).normalized;
        _lastProcessedStep = -1;

        if (_drumTrack != null)
        {
           _drumTrack.RegisterMineNode(this);
            _drumTrack.OnLoopBoundary += HandleLoopBoundary;
        }

        var dust = GetComponent<MineNodeDustInteractor>();
        if (dust != null)
        {
            dust.SetLevelAuthority(_drumTrack);
        }
        _carvedPath.Clear();
        if (track != null) ConfigureFromRole(track.assignedRole);

        // Visuals
        if (coreSprite != null)
        {
            // Prefer locked tint if you are feeding it from preview shard selection.
            coreSprite.color = tint;
        }

        // Register MineNode only
        if (_drumTrack != null)
        {
            _drumTrack.RegisterMineNode(this);
        }
    }

    private void HandleLoopBoundary()
    { if (_drumTrack == null) return;
        if (!pruneCarvedPathOnLoopBoundary) return; 
        if (_carvedPath == null || _carvedPath.Count < pruneMinPathCount) return; 
        int before = _carvedPath.Count;
        // Keep only cells that are still clear (no dust present).
        // This makes _carvedPath represent the *currently-open corridor*, not historical traversal.
        var compact = new List<Vector2Int>(before);
        Vector2Int? last = null;
        for (int i = 0; i < _carvedPath.Count; i++) {
            var cell = _carvedPath[i];
            // If dust has regrown here, it is no longer part of the active corridor.
            if (_drumTrack.HasDustAt(cell)) continue;
            // Avoid duplicates from lingering in a cell.
            if (last.HasValue && last.Value == cell) continue;
            compact.Add(cell);
            last = cell;
        }
        if (compact.Count == before) return; // no changes
        _carvedPath.Clear(); 
        _carvedPath.AddRange(compact);
    }
    public float GetCorridorHealDelaySeconds()
    {
        // Example tunables (make them per-role later if desired)
        float slow = 2.5f;  // low note
        float fast = 0.4f;  // high note
        return Mathf.Lerp(slow, fast, _lastNote01); // note01 high => fast
    }

    private float GetRoleTurnAngleDeg(MusicalRole role)
    {
        switch (role)
        {
            case MusicalRole.Bass:    return turnAngleBass;
            case MusicalRole.Groove:  return turnAngleGroove;
            case MusicalRole.Harmony: return turnAngleHarmony;
            case MusicalRole.Lead:    return turnAngleLead;
            default:                  return 18f;
        }
    }

    private static Vector2 Rotate(Vector2 v, float degrees)
    {
        float r = degrees * Mathf.Deg2Rad;
        float c = Mathf.Cos(r);
        float s = Mathf.Sin(r);
        return new Vector2(c * v.x - s * v.y, s * v.x + c * v.y);
    }
    
    public void NotifyDustErodedAt(Vector3 worldPos)
    {
        if (_drumTrack == null) return;

        // Project carving position into the grid
        Vector2Int cell = _drumTrack.CellOf(worldPos);

        // Avoid duplicate samples when staying in the same cell
        if (_carvedPath.Count > 0 && _carvedPath[_carvedPath.Count - 1] == cell)
            return;

        _carvedPath.Add(cell);
    }

    void ConfigureFromRole(MusicalRole role)
    {
        var dust = GetComponent<MineNodeDustInteractor>();
        if (!dust) return;

        switch (role)
        {
            case MusicalRole.Bass:
                // big, clear trench – more appetite, frequent carving
                dust.ConfigureCarving(intervalSeconds: 0.05f, appetiteMul: 1.4f);
                break;

            case MusicalRole.Groove:
                // medium appetite, slightly slower – rhythm-y “dotted” path
                dust.ConfigureCarving(intervalSeconds: 0.09f, appetiteMul: 1.0f);
                break;

            case MusicalRole.Harmony:
                // lighter carving, slower – subtle lace
                dust.ConfigureCarving(intervalSeconds: 0.11f, appetiteMul: 0.8f);
                break;

            case MusicalRole.Lead:
                // sharp, high-contrast nibble – narrow but insistent
                dust.ConfigureCarving(intervalSeconds: 0.07f, appetiteMul: 1.1f);
                break;
        }
    }


    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (_depletedHandled) return;
        if (!coll.gameObject.TryGetComponent<Vehicle>(out var vehicle))
            return;

        // Preview note (optional)
        TryPlayPreviewNote();

        // Apply damage
        _strength -= vehicle.GetForceAsDamage();
        _strength = Mathf.Max(0, _strength);

        float normalized = (maxStrength > 0) ? (float)_strength / maxStrength : 0f;
        float scaleFactor = Mathf.Lerp(0.2f, 1.1f, normalized);
        StartCoroutine(ScaleSmoothly(_originalScale * scaleFactor, 0.1f));
        // Deplete -> burst -> resolve
        if(_strength <= 0)
            HandleDepleted(vehicle);
    }
    void HandleDepleted(Vehicle vehicle)
    {
        _depletedHandled = true;

        // burst spawn: your existing call site is good; just make it depend on (_track, _noteSet)
        var origin = transform.position;
        var repelFrom = vehicle != null ? vehicle.transform.position : origin;

        _track.SpawnCollectableBurst(_noteSet, 8, -1, origin, repelFrom, 4.0f, 140f, 0.18f);

        TriggerExplosion();
    }

    void TryPlayPreviewNote()
    {
        if (_track == null || _noteSet == null || _drumTrack == null) return;

        int stepNow = _drumTrack.currentStep;
        int note = _noteSet.GetNoteForPhaseAndRole(_track, stepNow);
        _track.PlayNote(note, 180, 0.9f);
    }
    private void PlayExplosionChord(InstrumentTrackController ctrl, NoteSet ns, int stepNow, int durationTicks, float velocity)
    {
        if (ctrl == null || ns == null || ctrl.tracks == null) return;

        // Keep it readable: Bass + Harmony + Lead is usually enough.
        // Groove can be omitted or used as an octave/doubling if desired.
        for (int i = 0; i < ctrl.tracks.Length; i++)
        {
            var t = ctrl.tracks[i];
            if (t == null) continue;

            // Optional: restrict which roles participate in the "chord"
            if (t.assignedRole != MusicalRole.Bass &&
                t.assignedRole != MusicalRole.Harmony &&
                t.assignedRole != MusicalRole.Lead)
            {
                continue;
            }

            int note = ns.GetNoteForPhaseAndRole(t, stepNow);
            t.PlayNote(note, durationTicks, velocity);
        }
    }
    private IEnumerator CleanupAndDestroy()
    {
        yield return null;

        var dt = _drumTrack;
        if (dt != null)
        {
            dt.OnLoopBoundary -= HandleLoopBoundary;
            _drumTrack.FreeSpawnCell(_spawnCell.x, _spawnCell.y); 
            dt.UnregisterMineNode(this); // if you have it
        }
        Destroy(gameObject);
    }


    private void TriggerExplosion()
    {
        Debug.Log($"Triggering Explosion in Mine Node");
        var explosion = GetComponent<Explode>();
        if(explosion != null) explosion.Permanent(false);
        
        // 🔔 Notify listeners (PhaseStar) of the outcome kind and payload
        FireResolvedOnce();
        StartCoroutine(CleanupAndDestroy());
    }
    private void FireResolvedOnce()
    {
        if (_resolvedFired) return;
        _resolvedFired = true;
        OnResolved?.Invoke(this);
    }

    private void OnDisable() { 
        Debug.Log($"[MineNode] OnDisable {name} ({GetInstanceID()})"); 
        // Defensive: if we're disabled without CleanupAndDestroy running, unsubscribe here.
        if (_drumTrack != null) _drumTrack.OnLoopBoundary -= HandleLoopBoundary;
    }
    private void OnDestroy() {
        Debug.Log($"[MineNode] OnDestroy {name} ({GetInstanceID()})"); 
        // Defensive: ensure no stale event subscriptions survive destruction.
        if (_drumTrack != null) _drumTrack.OnLoopBoundary -= HandleLoopBoundary;
        // If we were destroyed unexpectedly, also try to remove from active list.
        if (_drumTrack != null) _drumTrack.activeMineNodes.Remove(this);
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

        if (transform.localScale.magnitude <= 0.05f)
            
            TriggerExplosion();
    } 
    void OnEnable() { 
         
        _col = GetComponent<Collider2D>(); 
        _rb  = GetComponent<Rigidbody2D>(); 
        if (_col != null) _col.enabled = true; // ✅ ensure interactable
        if (_rb  != null) _rb.simulated = true;
        }
}

