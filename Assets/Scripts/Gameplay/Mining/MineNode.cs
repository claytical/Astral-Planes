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
    private MineNodeRailAgent _rail;
    private Collider2D _col;
    private Rigidbody2D _rb;
    private float _spawnTime;
    [SerializeField] private float minCarveSeconds = 1.25f;

    private bool _retiredUseRail;
    // --- Carving → Retired lifecycle data ---
    private bool _isRetired = false;
    private int _birthLoop;
    private DrumTrack _drumTrack;

    // --- Grid-based path recording (only during carving) ---
    // Each entry is a grid cell the node occupied while carving.
    private readonly List<Vector2Int> _carvedPath = new List<Vector2Int>();

    // --- Ping-pong traversal state for Retired mode ---
    private int _pathIndex = 0;
    private bool _pathForward = true;

    // Retired steering parameters (grid patrol, modulated by vehicle hits)
    private const float _retiredSteerForceBase        = 6f;
    private const float _retiredSteerForceMin         = 2f;
    private const float _retiredSteerForceMax         = 14f;
    private const float _retiredSteerRecoverRate      = 8f;   // units/sec back toward base
    private float       _retiredSteerForce            = _retiredSteerForceBase;

    public event System.Action<MinedObjectType, MinedObjectSpawnDirective> OnResolved;
    private void Start()
    {
        _rb = GetComponent<Rigidbody2D>();
        _originalScale = transform.localScale;
        _strength = maxStrength;
    }

    private void FixedUpdate()
    {
        if (!_isRetired) return;
        if (_retiredUseRail) return;
        if (_rb == null || _drumTrack == null || _carvedPath.Count < 2) return;

        // Gradually recover steering force toward the base over time
        _retiredSteerForce = Mathf.MoveTowards(
            _retiredSteerForce,
            _retiredSteerForceBase,
            _retiredSteerRecoverRate * Time.fixedDeltaTime);

        // Current patrol target: center of the current grid cell
        Vector2Int cell  = _carvedPath[_pathIndex];
        Vector3    target = _drumTrack.CellCenter(cell);

        Vector2 toTarget = (Vector2)(target - (Vector3)_rb.position);
        if (toTarget.sqrMagnitude > 0.0001f)
        {
            Vector2 dir = toTarget.normalized;
            _rb.AddForce(dir * _retiredSteerForce, ForceMode2D.Force);
        }

        // Ping-pong index logic once we're close to the current target cell
        if (Vector2.Distance(_rb.position, target) < 0.1f)
        {
            if (_pathForward)
            {
                if (_pathIndex < _carvedPath.Count - 1)
                {
                    _pathIndex++;
                }
                else
                {
                    _pathForward = false;
                    _pathIndex--;
                }
            }
            else
            {
                if (_pathIndex > 0)
                {
                    _pathIndex--;
                }
                else
                {
                    _pathForward = true;
                    _pathIndex++;
                }
            }
        }
    }

    public void Initialize(MinedObjectSpawnDirective directive)
    {
        Debug.Log($"[MNDBG] MineNode.Initialize: node={name}, role={directive.role}, " +
                  $"type={directive.minedObjectType}, track={directive.assignedTrack?.name}, " +
                  $"spawnCell={directive.spawnCell}");
        _spawnTime = Time.time;
        _directive    = directive;                 // ⬅ cache
        GameObject obj = Instantiate(directive.minedObjectPrefab, transform.position, Quaternion.identity, transform);
        _lockedColor = directive.displayColor;
        _minedObject = obj.GetComponent<MinedObject>();
        obj.transform.SetParent(transform);
        obj.SetActive(false);
        _minedObject.Initialize(directive.minedObjectType, directive.assignedTrack, directive.noteSet, directive.trackModifierType);
        var dust = GetComponent<MineNodeDustInteractor>();
        if (dust != null)
        {
            Debug.Log($"[MNDBG] MineNode.Init tuning: node={name}, " +
                      $"carveInterval={dust.carveIntervalSeconds:F3}, appetiteMul={dust.carveAppetiteMul:F2}, " +
                      $"speedCapMul={dust.speedCapMul:F2}, extraBrake={dust.extraBrake:F2}");
        }
        else
        {
            Debug.LogWarning($"[MNDBG] MineNode.Init: node={name} has NO MineNodeDustInteractor");
        }

        // --- Capture drum track reference & birth loop index ---
        _drumTrack = _minedObject.assignedTrack?.drumTrack;
        if (_drumTrack != null)
        {
            _birthLoop = _drumTrack.completedLoops;
            _drumTrack.OnLoopBoundary += HandleLoopBoundary;
        }
        if (_rail == null) _rail = GetComponent<MineNodeRailAgent>();
        if (_rail != null)
        {
            _rail.SetDrumTrack(_drumTrack); // safe even if disabled
            _rail.enabled = false;          // carving phase
        }


// --- Begin in Carving Mode ---
        _isRetired = false;
        _carvedPath.Clear();

// --- Role-based carving parameters ---
        ConfigureFromRole(directive.role);

        
        coreSprite.color = directive.assignedTrack.trackColor;
        _minedObject.assignedTrack.drumTrack.RegisterMinedObject(_minedObject);
//        _minedObject.assignedTrack.drumTrack.OccupySpawnGridCell(directive.spawnCell.x, directive.spawnCell.y, GridObjectType.Node);
        // NoteSpawn carries a Ghost trigger
//        NoteSpawnerMinedObject spawner = obj.GetComponent<NoteSpawnerMinedObject>();
        _minedObject.assignedTrack.drumTrack.activeMineNodes.Add(this);
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
    {
        if (_isRetired || _drumTrack == null) return;

        // Don’t allow immediate retirement if we spawned near the end of a loop.
        if (Time.time - _spawnTime < minCarveSeconds)
            return;

        if (_drumTrack.completedLoops > _birthLoop)
            EnterRetiredMode();
    }

    private void EnterRetiredMode()
    {
        // ------------------------------------------------------------
        // 1) Flip to retired + stop carving
        // ------------------------------------------------------------
        _isRetired = true;
        _retiredSteerForce = _retiredSteerForceBase;

        var dust = GetComponent<MineNodeDustInteractor>();
        if (dust != null) dust.carveMaze = false;

        // Queue this corridor to close when the NEXT MineNode is spawned.
        var gen = GameFlowManager.Instance != null ? GameFlowManager.Instance.dustGenerator : null;
        if (gen != null && _carvedPath != null && _carvedPath.Count >= 2)
            gen.EnqueueCorridorForNextNodeSpawn(_carvedPath);

        // ------------------------------------------------------------
        // 2) Prefer rail-based "component patrol" if available
        // ------------------------------------------------------------
        if (_rail == null) _rail = GetComponent<MineNodeRailAgent>();
        _retiredUseRail = (_rail != null && _drumTrack != null && _rb != null);

        if (_retiredUseRail)
        {
            // IMPORTANT: rail locomotion should only run during retirement.
            // If you disabled the rail agent during carving, re-enable it here.
            _rail.enabled = true;

            // Ensure rail agent has the track (needed for reachability + world<->grid).
            _rail.SetDrumTrack(_drumTrack);

            // Target: farthest reachable cell within our current empty-space component.
            _rail.SetTargetProvider(() =>
            {
                var start = _drumTrack.CellOf(_rb.position);
                return _drumTrack.FarthestReachableCellInComponent(start);
            });

            // Plan + start moving.
            _rail.ReplanToFarthest();
            return; // do NOT also set up carved-path ping-pong
        }

        // ------------------------------------------------------------
        // 3) Fallback: ping-pong along carved path (no rail agent)
        // ------------------------------------------------------------
        if (_carvedPath == null || _carvedPath.Count < 2)
        {
            _pathIndex = 0;
            _pathForward = true;
            return;
        }

        _pathIndex = 0;
        _pathForward = true;
    }

    public void NotifyDustErodedAt(Vector3 worldPos)
    {
        if (_isRetired) return;     // retired nodes do not record
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
        
        if(_rail != null) _rail.ReplanToFarthest();
        if (_minedObject != null)
        {
            Debug.Log($"Found Object for Spawner: {_minedObject.name}");
            var spawner = _minedObject.GetComponent<NoteSpawnerMinedObject>(); 
            if (spawner != null) {
                // Normal note spawner feedback
                Debug.Log($"Playing collision sound");
                CollectionSoundManager.Instance?.PlayNoteSpawnerSound(spawner.assignedTrack, spawner.selectedNoteSet);
            }
            else {
                // Utility payload (remix ring etc.) — use a generic pickup cue
                Debug.Log($"Playing default sound");
                CollectionSoundManager.Instance?.PlayEffect(SoundEffectPreset.Aether);
            }          
            if (coll.gameObject.TryGetComponent<Vehicle>(out var vehicle))
            {
                if (_isRetired && _drumTrack != null && _carvedPath.Count >= 2)
                {
                    int fromIdx = _pathIndex;
                    int toIdx = _pathForward
                        ? Mathf.Min(_pathIndex + 1, _carvedPath.Count - 1)
                        : Mathf.Max(_pathIndex - 1, 0);

                    Vector2Int fromCell = _carvedPath[fromIdx];
                    Vector2Int toCell   = _carvedPath[toIdx];

                    Vector3 fromPos = _drumTrack.CellCenter(fromCell);
                    Vector3 toPos   = _drumTrack.CellCenter(toCell);

                    Vector2 patrolDir = (Vector2)(toPos - fromPos);
                    if (patrolDir.sqrMagnitude > 0.0001f)
                    {
                        patrolDir.Normalize();

                        // Choose impact vector: vehicle velocity if available, else collision relative velocity
                        Vector2 impactVel = Vector2.zero;
                        if (vehicle.TryGetComponent<Rigidbody2D>(out var vRb) &&
                            vRb.linearVelocity.sqrMagnitude > 0.0001f)
                        {
                            impactVel = vRb.linearVelocity;
                        }
                        else if (coll.relativeVelocity.sqrMagnitude > 0.0001f)
                        {
                            impactVel = coll.relativeVelocity;
                        }

                        if (impactVel.sqrMagnitude > 0.0001f)
                        {
                            impactVel.Normalize();
                            float dot = Vector2.Dot(patrolDir, impactVel);

                            const float alignThresh   = 0.4f;  // "hit from behind"
                            const float reverseThresh = -0.4f; // "hit head-on"

                            if (dot > alignThresh)
                            {
                                // Hit from behind: temporarily speed up along the patrol path
                                _retiredSteerForce = Mathf.Min(
                                    _retiredSteerForce * 1.5f,
                                    _retiredSteerForceMax);
                            }
                            else if (dot < reverseThresh)
                            {
                                // Head-on: slow down, and for strong head-on hits flip patrol direction
                                _retiredSteerForce = Mathf.Max(
                                    _retiredSteerForce * 0.5f,
                                    _retiredSteerForceMin);

                                if (dot < -0.8f)
                                {
                                    _pathForward = !_pathForward;
                                }
                            }
                        }
                    }
                }
                
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

                        burstTrack.SpawnCollectableBurst(spawner.selectedNoteSet, 8);

                        spawner.assignedTrack.DisplayNoteSet();
                        var set = spawner.assignedTrack != null ? spawner.assignedTrack.GetActiveNoteSet() : null; 
                        float remain = (spawner.assignedTrack != null && spawner.assignedTrack.drumTrack != null) ? spawner.assignedTrack.drumTrack.GetTimeToLoopEnd() : 0.25f; 
                        CollectionSoundManager.Instance?.PlayBurstLeadIn(spawner.assignedTrack, set, Mathf.Max(0.05f, remain));

                    }
                    else
                    {
                        Debug.Log($"Nothing to spawn, because spawner is null...");
                        // Utility payload path keeps your generic pickup cue
                        CollectionSoundManager.Instance?.PlayEffect(SoundEffectPreset.Aether);
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
    private IEnumerator CleanupAndDestroy()
    {
        // brief frame to ensure Reveal finishes toggles
        yield return null;

        var dt = _minedObject?.assignedTrack?.drumTrack;
        if (dt != null)
        {
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
        if(explosion != null) explosion.Permanent();
        
        // 🔔 Notify listeners (PhaseStar) of the outcome kind and payload
        FireResolvedOnce(_directive.minedObjectType, _directive);
//        RevealPreloadedObject();
        StartCoroutine(CleanupAndDestroy());
    }
    private void FireResolvedOnce(MinedObjectType kind, MinedObjectSpawnDirective dir)
    {
        if (_resolvedFired) return;
        _resolvedFired = true;
        try
        {
            Debug.Log($"Fire Resolved Once, trying to invoke resolution {kind} / {dir}...");
            OnResolved?.Invoke(kind, dir);
        }
        catch (System.Exception e) { Debug.LogException(e, this); }
    }
    private void OnDisable(){ Debug.Log($"[MineNode] OnDisable {name} ({GetInstanceID()})"); }
    private void OnDestroy(){ Debug.Log($"[MineNode] OnDestroy {name} ({GetInstanceID()})"); }
    // Call this right after you instantiate the child mined object (inside MineNode or the spawner).
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
        _rail = GetComponent<MineNodeRailAgent>(); 
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
