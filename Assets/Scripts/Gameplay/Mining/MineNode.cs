using System.Collections;
using System.Collections.Generic;
using Gameplay.Mining;
using UnityEngine;
using Random = System.Random;

public class MineNode : MonoBehaviour
{
    public SpriteRenderer coreSprite;
    public int maxStrength = 100;

    private int _strength;
    private GameObject _preloadedObject;
    private Vector3 _originalScale;
    private Color? _lockedColor;
    public MinedObject _minedObject;
    private bool _objectRevealed, _depletedHandled, _resolvedFired;
    private MinedObjectSpawnDirective _directive;
    private Collider2D _col;
    private Rigidbody2D _rb;
    private float _spawnTime;
    // Cached normalized pitch (0 = lowest note, 1 = highest note)
    private float _lastNote01 = 0.5f;

    [SerializeField] private float minCarveSeconds = 1.25f;

    private int _birthLoop;
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
    private InstrumentTrack _assignedTrack;
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

    public event System.Action<MinedObjectType, MinedObjectSpawnDirective> OnResolved;
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
        _assignedTrack == null)
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

        int note = _noteSet.GetNoteForPhaseAndRole(_assignedTrack, stepNow);

        float note01 = Mathf.InverseLerp(
            _assignedTrack.lowestAllowedNote,
            _assignedTrack.highestAllowedNote,
            note);

        _lastNote01 = note01;

        float baseAngle = GetRoleTurnAngleDeg(_role);

        // Scale turn magnitude by pitch + whether this step is authored as a turn.
        float turnAngle = Mathf.Lerp(baseAngle * 0.5f, baseAngle * 1.25f, note01) * turnGate;
        float delta = UnityEngine.Random.Range(-turnAngle, turnAngle);

        _carveDir = Rotate(_carveDir, delta).normalized;
    }

    // --- NOTE-DRIVEN SPEED ---
    int currentNote = _noteSet.GetNoteForPhaseAndRole(_assignedTrack, stepNow);

    float speed01 = Mathf.InverseLerp(
        _assignedTrack.lowestAllowedNote,
        _assignedTrack.highestAllowedNote,
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

    public Color GetImprintColor()
    {
        // InstrumentTrack is the semantic source of color
        if (_assignedTrack != null)
            return _assignedTrack.trackColor;

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
        return _noteSet.GetNoteForPhaseAndRole(_assignedTrack, step);
    }

    public void Initialize(MinedObjectSpawnDirective directive)
    {
        Debug.Log($"[MNDBG] MineNode.Initialize: node={name}, role={directive.role}, " +
                  $"type={directive.minedObjectType}, track={directive.assignedTrack?.name}, " +
                  $"spawnCell={directive.spawnCell}");
        _spawnTime = Time.time;
        _directive    = directive;            
        _noteSet       = directive.noteSet;
        _assignedTrack = directive.assignedTrack;
        _role          = directive.role;
        GameObject obj = Instantiate(directive.minedObjectPrefab, transform.position, Quaternion.identity, transform);
        _lockedColor = directive.displayColor;
        _minedObject = obj.GetComponent<MinedObject>();
        obj.transform.SetParent(transform);
        obj.SetActive(false);
        float a = UnityEngine.Random.Range(0f, 360f);
        _carveDir = new Vector2(Mathf.Cos(a * Mathf.Deg2Rad), Mathf.Sin(a * Mathf.Deg2Rad)).normalized;
        _lastProcessedStep = -1;
        _minedObject.Initialize(directive.minedObjectType, directive.assignedTrack, directive.noteSet, directive.trackModifierType);
        // --- Capture drum track reference & birth loop index ---
        _drumTrack = _minedObject.assignedTrack?.drumTrack;
        if (_drumTrack != null)
        {
            _birthLoop = _drumTrack.completedLoops;
            _drumTrack.OnLoopBoundary += HandleLoopBoundary;
        }
        var dust = GetComponent<MineNodeDustInteractor>();
        if (dust != null)
        {
            dust.SetLevelAuthority(_drumTrack);
        }
        else
        {
            Debug.LogWarning($"[MNDBG] MineNode.Init: node={name} has NO MineNodeDustInteractor");
        }

        _carvedPath.Clear();
        ConfigureFromRole(directive.role);
        coreSprite.color = directive.assignedTrack.trackColor;
        _drumTrack.RegisterMinedObject(_minedObject);
        _drumTrack.RegisterMineNode(this);
        TrackUtilityMinedObject track = obj.GetComponent<TrackUtilityMinedObject>();
        if (track != null)
        {
            this._preloadedObject = obj;
            if (directive.remixUtility != null)
                track.Initialize(GameFlowManager.Instance.controller, directive);
        }

        if (coreSprite != null)
        {
            Debug.Log($"It's not null...");
            // prefer directive.displayColor; otherwise use the assigned track's color
            var fallback = (_minedObject != null && _minedObject.assignedTrack != null)
                ? (Color?)_minedObject.assignedTrack.trackColor
                : null;

            var finalColor = _lockedColor ?? fallback;
            Debug.Log($"It's not null... fallback: {fallback}, finalColor: {finalColor}");

            if (finalColor.HasValue) coreSprite.color = finalColor.Value;
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

    private void RevealPreloadedObject()
    {
        if (_preloadedObject == null || _preloadedObject.gameObject == null || _objectRevealed)
        {
            Debug.LogWarning("⚠️ Preloaded object was destroyed before reveal.");
            return;
        }

        Debug.Log($"Reveal Preloaded Object: {_preloadedObject.name}");

        // Detach so the payload survives when the node is destroyed
        _preloadedObject.transform.SetParent(null, true);

        // Position the payload at the node and show it
        _preloadedObject.transform.position = transform.position;
        _preloadedObject.SetActive(true);
        _objectRevealed = true;
    }

    private void OnCollisionEnter2D(Collision2D coll)
    { 
        
        if (_minedObject != null)
        {
            Debug.Log($"Found Object for Spawner: {_minedObject.name}");
            var spawner = _minedObject.GetComponent<NoteSpawnerMinedObject>(); 
            if (spawner != null)
            {
                int stepNow = (_drumTrack != null) ? _drumTrack.currentStep : 0;

                // Prefer the NoteSpawner's selected set; fall back to the directive-provided set.
                var ns = (spawner.selectedNoteSet != null) ? spawner.selectedNoteSet : _directive?.noteSet;

                // Prefer spawner track; fall back to minedObject track.
                var tr = (spawner.assignedTrack != null) ? spawner.assignedTrack : _minedObject.assignedTrack;

                if (ns != null && tr != null)
                {
                    int note = ns.GetNoteForPhaseAndRole(tr, stepNow);

                    // InstrumentTrack signature is: PlayNote(int note, int durationTicks, float velocity)
                    // durationTicks: 120–240 is usually a short audible preview without feeling “stuck”.
                    tr.PlayNote(note, 180, 0.9f);
                }

                // IMPORTANT: This currently plays a random step internally, which will conflict with the step preview.
                // So do NOT call it here unless you update CollectionSoundManager to accept stepNow.
                // CollectionSoundManager.Instance?.PlayNoteSpawnerSound(tr, ns);
            }


            else {
                // Utility payload (remix ring etc.) — use a generic pickup cue
                Debug.Log($"Playing default sound");
                CollectionSoundManager.Instance?.PlayEffect(SoundEffectPreset.Aether);
            }          
            if (coll.gameObject.TryGetComponent<Vehicle>(out var vehicle))
            {
                _strength -= vehicle.GetForceAsDamage();
                _strength = Mathf.Max(0, _strength); // Ensure it doesn’t go below 0
                float normalized = (float)_strength / maxStrength; // [0, 1]
                float scaleFactor = Mathf.Lerp(.2f, 1.1f, normalized); // Linear scale from 1 to 0
                Debug.Log($"Strength: {_strength}, Normalized: {normalized}, Scale: {scaleFactor}");

                StartCoroutine(ScaleSmoothly(_originalScale * scaleFactor, 0.1f));
                if (_strength <= 0 && !_depletedHandled)
                {
                    Debug.Log($"No more strength...");
                    _depletedHandled = true;
                    // Fresh burst for this node
                    // Try to spawn notes if this payload is a NoteSpawner
                    if (spawner != null)
                    {
                        // Ensure there is a NoteSet
                        if (spawner.selectedNoteSet == null)
                        {
                            Debug.Log($"No note set found, creating new one");
                            // Prefer any directive you cached; otherwise rebuild from track/phase
                            var track = spawner.assignedTrack ?? _minedObject.assignedTrack;
                            
                            if (track != null)
                            { 
                                var ptm   = GameFlowManager.Instance.phaseTransitionManager;
                                var motif = ptm.currentMotif;
                                Debug.Log($"[MINENODE] Generating Motif Noteset");
                                var ns = ptm.noteSetFactory.Generate(track, ptm.currentMotif);
                                spawner.selectedNoteSet = ns;
                                Debug.Log($"Note set: {ns}");
                                if (motif != null)
                                {
                                    int fuseLoops = motif.GetLoopsToAscendFor(track.assignedRole, fallback: track.ascendLoopCount);
                                    track.ascendLoopCount = Mathf.Max(1, fuseLoops);
                                    Debug.Log($"[MINENODE] Set ascendLoopCount for {track.name} ({track.assignedRole}) to {track.ascendLoopCount} from motif {motif.name}");

                                }
                            }
                        }
                        // Emit notes BEFORE we reveal/destroy anything
                        Debug.Log($"Bursting Collectables");
                        if (spawner == null || spawner.assignedTrack == null || spawner.selectedNoteSet == null) { 
                            Debug.LogWarning("[MineNode] Note burst aborted: missing spawner/track/noteSet."); 
                            return;
                        } 
                        InstrumentTrack burstTrack = spawner.assignedTrack;

                        Debug.Log($"Bursting Collectables with {spawner.selectedNoteSet} at {burstTrack}");

                        var ctrl = GameFlowManager.Instance != null ? GameFlowManager.Instance.controller : null;
                        if (ctrl != null && burstTrack != null)
                        {
                            // Repeat-role heuristic: if this track already has any notes (or bin 0 filled),
                            // then catching another MineNode of this role is allowed to expand the loop.
                            var notes = burstTrack.GetPersistentLoopNotes();
                            bool hasAnyNotes = (notes != null && notes.Count > 0);
                            bool bin0Filled  = burstTrack.IsBinFilled(0);

                            if (hasAnyNotes || bin0Filled)
                            {
                                ctrl.AllowAdvanceNextBurst(burstTrack);
                                Debug.Log($"[MineNode] AllowAdvanceNextBurst: {burstTrack.name} ({burstTrack.assignedRole})");
                            }
                            else
                            {
                                Debug.Log($"[MineNode] First notes for {burstTrack.name} ({burstTrack.assignedRole}) — no advance.");
                            }
                        }

                        // Void-burst intent: originate at MineNode death position and eject away from the impacting vehicle.
                        Vector3 origin    = transform.position; 
                        Vector3 repelFrom = vehicle != null ? vehicle.transform.position : origin; 
                        // Conservative defaults for playtesting; tune later.
                        float impulse = 4.0f;     // readable ejecta push
                        float spread  = 140f;     // mostly away from player, still organic
                        float jitter  = 0.18f;    // avoid stacking at exact cell centers
                        burstTrack.SpawnCollectableBurst(spawner.selectedNoteSet, 8, -1, origin, repelFrom, impulse, spread, jitter);
                        spawner.assignedTrack.DisplayNoteSet();
                        var set = spawner.assignedTrack != null ? spawner.assignedTrack.GetActiveNoteSet() : null; 
                        float remain = (spawner.assignedTrack != null && spawner.assignedTrack.drumTrack != null) ? spawner.assignedTrack.drumTrack.GetTimeToLoopEnd() : 0.25f; 
                        CollectionSoundManager.Instance?.PlayBurstLeadIn(spawner.assignedTrack, set, Mathf.Max(0.05f, remain));
                        int chordTicks = 240;      // ~1/2 beat at 480ppq (adjust to taste)
                        float vel = 0.95f;

                        PlayExplosionChord(ctrl, spawner.selectedNoteSet, _drumTrack.currentStep, chordTicks, vel);
                    }
                    else
                    {
                        Debug.Log($"Nothing to spawn, because spawner is null...");
                        // Utility payload path keeps your generic pickup cue
//                        CollectionSoundManager.Instance?.PlayEffect(SoundEffectPreset.Aether);
                    }

                    // Reveal any preloaded object AFTER spawning
                    if (_preloadedObject != null)
                    {
                        Debug.Log($"Revealing Preloaded object");
                        _preloadedObject.transform.SetParent(null, true); // keep world pos
                        _preloadedObject.transform.localScale = _originalScale;
                        _preloadedObject.SetActive(true);
                    }

                    // Now run your existing VFX/cleanup
                    TriggerExplosion(); // destroy self, fade sprite, etc.
                }
                else
                {
                    TriggerPreExplosion();
                }
            }
        }
        else
        {
            Debug.Log($"no object found for {gameObject.name}");

        }
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
        // brief frame to ensure Reveal finishes toggles
        yield return null;

        var dt = _minedObject?.assignedTrack?.drumTrack;
        if (dt != null)
        {
            dt.OnLoopBoundary -= HandleLoopBoundary;
            // Free the reserved grid cell for future spawns
            dt.FreeSpawnCell(_directive.spawnCell.x, _directive.spawnCell.y);

            // Remove this node from the active list
            dt.activeMineNodes.Remove(this);
        }

        Destroy(gameObject);
    }
    
    
    private void TriggerPreExplosion()
    {
        var explosion = GetComponent<Explode>();
        if(explosion != null) explosion.PreExplosion();
    }
    private void TriggerExplosion()
    {
        Debug.Log($"Triggering Explosion in Mine Node");
        var explosion = GetComponent<Explode>();
        if(explosion != null) explosion.Permanent(false);
        
        // 🔔 Notify listeners (PhaseStar) of the outcome kind and payload
        FireResolvedOnce(_directive.minedObjectType, _directive);
        StartCoroutine(CleanupAndDestroy());
    }
    private void FireResolvedOnce(MinedObjectType kind, MinedObjectSpawnDirective dir)
    {
        Debug.Log($"[MNDBG] FireResolvedOnce: type={kind} directiveTrack={dir?.assignedTrack?.name} directivePrefab={dir?.minedObjectPrefab?.name}");

        if (_resolvedFired) return;
        _resolvedFired = true;
        try
        {
            Debug.Log($"Fire Resolved Once, trying to invoke resolution {kind} / {dir}...");
            OnResolved?.Invoke(kind, dir);
        }
        catch (System.Exception e) { Debug.LogException(e, this); }
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

public class MinedObjectSpawnDirective
{
    public MinedObjectType minedObjectType;
    public MusicalRole role;
    public InstrumentTrack assignedTrack;
    public RemixUtility remixUtility;
    public NoteSet noteSet;
    public TrackModifierType trackModifierType;
    public MusicalRoleProfile roleProfile;         // object (optional but preferred)
    
    public Color displayColor;
    public GameObject prefab;
    public GameObject minedObjectPrefab;
    public Vector2Int spawnCell;
}
