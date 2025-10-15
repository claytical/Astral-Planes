using System.Collections;
using Gameplay.Mining;
using UnityEngine;

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
    public event System.Action<MinedObjectType, MinedObjectSpawnDirective> OnResolved;
    private void Start()
    {
        GetComponent<Rigidbody2D>();
        _originalScale = transform.localScale;
        _strength = maxStrength;
    }
    public void Initialize(MinedObjectSpawnDirective directive)
    {
        _directive    = directive;                 // ⬅ cache
        GameObject obj = Instantiate(directive.minedObjectPrefab, transform.position, Quaternion.identity, transform);
        _lockedColor = directive.displayColor;
        _minedObject = obj.GetComponent<MinedObject>();
        obj.transform.SetParent(transform);
        obj.SetActive(false);
        _minedObject.Initialize(directive.minedObjectType, directive.assignedTrack, directive.noteSet, directive.trackModifierType);
        coreSprite.color = directive.assignedTrack.trackColor;
        _minedObject.assignedTrack.drumTrack.RegisterMinedObject(_minedObject);
        _minedObject.assignedTrack.drumTrack.OccupySpawnGridCell(directive.spawnCell.x, directive.spawnCell.y, GridObjectType.Node);
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
        Debug.Log($"Hit MineNode: {coll.gameObject.name} object revealed? {_objectRevealed}");
        if (_objectRevealed)
        {
            Debug.Log($"Mine Node Object Already Revealed");
            return;
        }
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
                _strength -= vehicle.GetForceAsDamage();
                _strength = Mathf.Max(0, _strength); // Ensure it doesn’t go below 0
                float normalized = (float)_strength / maxStrength; // [0, 1]
                float scaleFactor = Mathf.Lerp(0f, 1f, normalized); // Linear scale from 1 to 0
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

                                var ns = GameFlowManager.Instance.noteSetFactory.Generate(track, GameFlowManager.Instance.phaseTransitionManager.currentPhase);
                                spawner.selectedNoteSet = ns;
                                Debug.Log($"Note set: {ns}");

                            }
                        }

                        // 🔊 (you already play SFX above; keep or remove to avoid double)
                        // CollectionSoundManager.Instance?.PlayNoteSpawnerSound(spawner.assignedTrack, spawner.selectedNoteSet);

                        // Emit notes BEFORE we reveal/destroy anything
                        Debug.Log($"Bursting Collectables");
                        spawner.assignedTrack.SpawnCollectableBurst(spawner.selectedNoteSet);
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
    private void TriggerExplosion()
    {
        Debug.Log($"Triggering Explosion in Mine Node");
        // 🔔 Notify listeners (PhaseStar) of the outcome kind and payload
        FireResolvedOnce(_directive.minedObjectType, _directive);
        RevealPreloadedObject();
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
