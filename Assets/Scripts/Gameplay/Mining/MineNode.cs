using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Effects;
using Gameplay.Mining;
using UnityEngine;



public class MineNode : MonoBehaviour
{
    public SpriteRenderer coreSprite;
    public int maxStrength = 100;
    private int strength;
    private GameObject preloadedObject;
    private Vector3 originalScale;
    private bool objectRevealed = false;
    private Color? lockedColor;
    private Rigidbody2D rb;
    private MinedObject minedObject;
    private bool _depletedHandled;
    public event System.Action<MinedObjectType, MinedObjectSpawnDirective> OnResolved;
    private bool _resolvedFired;
    private MinedObjectSpawnDirective _directive;  // cache the directive
    private MinedObjectType _carriedType;          // cache the type
    private void Start()
    {
        rb = GetComponent<Rigidbody2D>();
        originalScale = transform.localScale;
        strength = maxStrength;
    }
    private void FireResolvedOnce(MinedObjectType kind, MinedObjectSpawnDirective dir)
    {
        if (_resolvedFired) return;
        _resolvedFired = true;
        try { OnResolved?.Invoke(kind, dir); }
        catch (System.Exception e) { Debug.LogException(e, this); }
    }
    public void Initialize(MinedObjectSpawnDirective directive)
    {
        _directive    = directive;                 // ⬅ cache
        _carriedType  = directive.minedObjectType; // ⬅ cache

        GameObject obj = Instantiate(directive.minedObjectPrefab, transform.position, Quaternion.identity, transform);
        lockedColor = directive.displayColor;
        minedObject = obj.GetComponent<MinedObject>();
        obj.transform.SetParent(transform);
        obj.SetActive(false);
        minedObject.Initialize(directive.minedObjectType, directive.assignedTrack, directive.noteSet, directive.trackModifierType);
        minedObject.assignedTrack.drumTrack.RegisterMinedObject(minedObject);
        minedObject.assignedTrack.drumTrack.OccupySpawnGridCell(directive.spawnCell.x, directive.spawnCell.y, GridObjectType.Node);
        // NoteSpawn carries a Ghost trigger
        NoteSpawnerMinedObject spawner = obj.GetComponent<NoteSpawnerMinedObject>();
        minedObject.assignedTrack.drumTrack.activeMineNodes.Add(this);
//        if (spawner != null) this.preloadedObject = spawner.ghostTrigger;

        // TrackUtility carries itself
        TrackUtilityMinedObject track = obj.GetComponent<TrackUtilityMinedObject>();
        if (track != null)
        {
            this.preloadedObject = obj;
            if (directive.remixUtility != null)
                track.Initialize(GameFlowManager.Instance.controller, directive);
        }

        if (coreSprite != null)
        {
            Debug.Log($"It's not null...");
            // prefer directive.displayColor; otherwise use the assigned track's color
            var fallback = (minedObject != null && minedObject.assignedTrack != null)
                ? (Color?)minedObject.assignedTrack.trackColor
                : null;

            var finalColor = lockedColor ?? fallback;
            Debug.Log($"It's not null... fallback: {fallback}, finalColor: {finalColor}");

            if (finalColor.HasValue) coreSprite.color = finalColor.Value;
        }

    }

    private void OnCollisionEnter2D(Collision2D coll)
    { 
        if (objectRevealed) return; 
        var spawner = minedObject.GetComponent<NoteSpawnerMinedObject>(); 
        if (spawner != null) {
            // Normal note spawner feedback
            CollectionSoundManager.Instance?.PlayNoteSpawnerSound(spawner.assignedTrack, spawner.selectedNoteSet);
        }
        else {
            // Utility payload (remix ring etc.) — use a generic pickup cue
            CollectionSoundManager.Instance?.PlayEffect(SoundEffectPreset.Aether);
        }
        if (coll.gameObject.TryGetComponent<Vehicle>(out var vehicle))
        {
            strength -= vehicle.GetForceAsDamage();
            strength = Mathf.Max(0, strength); // Ensure it doesn’t go below 0
            float normalized = (float)strength / maxStrength; // [0, 1]
            float scaleFactor = Mathf.Lerp(0f, 1f, normalized); // Linear scale from 1 to 0

            StartCoroutine(ScaleSmoothly(originalScale * scaleFactor, 0.1f));
            if (strength <= 0 && !_depletedHandled)
            {
                _depletedHandled = true;
                // Fresh burst for this node
                // Try to spawn notes if this payload is a NoteSpawner
                if (spawner != null)
                {
                    // Ensure there is a NoteSet
                    if (spawner.selectedNoteSet == null)
                    {
                        // Prefer any directive you cached; otherwise rebuild from track/phase
                        var track = spawner.assignedTrack ?? minedObject.assignedTrack;
                        var phase = track.drumTrack.currentPhase;
                        if (track != null && phase != null)
                        {
                            var ns = GameFlowManager.Instance.noteSetFactory.Generate(track, phase);
                            spawner.selectedNoteSet = ns;
                        }
                    }

                    // 🔊 (you already play SFX above; keep or remove to avoid double)
                    // CollectionSoundManager.Instance?.PlayNoteSpawnerSound(spawner.assignedTrack, spawner.selectedNoteSet);

                    // Emit notes BEFORE we reveal/destroy anything
                    spawner.assignedTrack.SpawnCollectableBurst(spawner.selectedNoteSet);
                }
                else
                {
                    // Utility payload path keeps your generic pickup cue
                    CollectionSoundManager.Instance?.PlayEffect(SoundEffectPreset.Aether);
                }

                // Reveal any preloaded object AFTER spawning
                if (preloadedObject != null)
                {
                    preloadedObject.transform.SetParent(null, true); // keep world pos
                    preloadedObject.transform.localScale = originalScale;
                    preloadedObject.SetActive(true);
                }

                // Now run your existing VFX/cleanup
                TriggerExplosion(); // destroy self, fade sprite, etc.
            }
            
        }
    }

    private void TriggerExplosion()
    {
        Debug.Log($"Triggering Explosion in Mine Node");
        //preloadedObject
        minedObject.assignedTrack.drumTrack.UnregisterMinedObject(minedObject);
        // 🔔 Notify listeners (PhaseStar) of the outcome kind and payload
        FireResolvedOnce(_directive.minedObjectType, _directive);
        StartCoroutine(DelayedReveal(0.2f));
    }

    private IEnumerator DelayedReveal(float delay)
    {
        yield return new WaitForSeconds(delay);
        RevealPreloadedObject();
    }

    private void RevealPreloadedObject()
    {
        if (preloadedObject == null || preloadedObject.gameObject == null || objectRevealed)
        {
            Debug.LogWarning("⚠️ Preloaded object was destroyed before reveal.");
            return;
        }

        Debug.Log($"Reveal Preloaded Object: {preloadedObject.name}");

        preloadedObject.transform.position = transform.position;

        if (preloadedObject.TryGetComponent(out MinedObject mined))
        {
            mined.EnableColliderAfterDelay(0.5f);
        }

        Destroy(gameObject);
        objectRevealed = true;
    }
    void OnDisable(){ Debug.Log($"[MineNode] OnDisable {name} ({GetInstanceID()})"); }
    void OnDestroy(){ Debug.Log($"[MineNode] OnDestroy {name} ({GetInstanceID()})"); }
    // Call this right after you instantiate the child mined object (inside MineNode or the spawner).

// PhaseStar (or MineNodeSpawner) — helper
    private void InitializeChildFromDirective(GameObject child, MinedObjectSpawnDirective dir)
    {
        // 1) Resolve role profile deterministically:
        // preference: directive.roleProfile → track.assignedRole → directive.role
        MusicalRoleProfile profile = dir.roleProfile
                                     ?? (dir.assignedTrack != null
                                         ? MusicalRoleProfileLibrary.GetProfile(dir.assignedTrack.assignedRole)
                                         : null)
                                     ?? MusicalRoleProfileLibrary.GetProfile(dir.role);

        // 2) Base component: enum role + shared fields
        var baseMO = child.GetComponent<MinedObject>();
        if (baseMO != null)
        {
            baseMO.assignedTrack   = dir.assignedTrack;
            baseMO.minedObjectType = dir.minedObjectType;
            baseMO.musicalRole     = dir.role;           // enum (Bass/Lead/Harmony/…)
            // (If you added a roleProfile field on MinedObject, assign it here too.)
        }

        // 3) NoteSpawner component: expects a PROFILE object
        var spawnerMO = child.GetComponent<NoteSpawnerMinedObject>();
        if (spawnerMO != null)
        {
            spawnerMO.assignedTrack   = dir.assignedTrack;
            spawnerMO.musicalRole     = profile;         // profile object, not enum
            spawnerMO.selectedNoteSet = dir.noteSet;     // must be non-null (see #4)
        }

        // 4) Color: lock to directive.displayColor (fallback to track color)
        var tint = (dir.displayColor.a > 0f)
            ? dir.displayColor
            : (dir.assignedTrack != null ? dir.assignedTrack.trackColor : Color.white);

        // Apply to the actual renderer used by the prefab
        var trackVisual = child.GetComponent<TrackItemVisual>();
        if (trackVisual != null) trackVisual.trackColor = tint;

        foreach (var sr in child.GetComponentsInChildren<SpriteRenderer>(true))
        {
            var c = tint; c.a = sr.color.a; // preserve existing alpha
            sr.color = c;
        }
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
}

[System.Serializable]
public enum MineNodeSelectionMode
{
    WeightedRandom,
    RoundRobinUniqueFirst,
    QuotaBased
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

[System.Serializable]
public class WeightedMineNode
{
    public MinedObjectType minedObjectType;
    public TrackModifierType trackModifierType;
    public MusicalRole role;

    [Range(0, 100)]
    public int weight = 1;
    
    public int quota = 1;

    [Tooltip("Leave empty for all phases")]
    public List<MusicalPhase> allowedPhases;

    [Tooltip("Higher = rarer")]
    public int rarityTier = 0;

    [Tooltip("If true, requires player to have collected at least one remix")]
    public bool requiresRemixToSpawn = false;

    public MinedObjectSpawnDirective ToDirective(
        InstrumentTrack track,
        NoteSet noteSet,
        Color color,
        MineNodePrefabRegistry nodeRegistry,
        MinedObjectPrefabRegistry objectRegistry,
        NoteSetFactory noteSetFactory,
        MusicalPhase currentPhase)
    {
        Debug.Log($"🧭 Spawning directive for {minedObjectType} / {trackModifierType}");

        RemixUtility remixUtil = null;

        var phaseManager = track?.drumTrack?.progressionManager;
        if (phaseManager != null)
        {
            int index = phaseManager.GetCurrentPhaseIndex();
            if (index >= 0 && index < phaseManager.phaseQueue.phaseGroups.Count)
            {
                var group = phaseManager.phaseQueue.phaseGroups[index];
                remixUtil = group.remixUtilities.FirstOrDefault(r => r.targetRole == track.assignedRole);
            }
        }

        NoteSet generatedNoteSet = null;

        if (minedObjectType == MinedObjectType.NoteSpawner && noteSetFactory != null)
        {
            generatedNoteSet = noteSetFactory.Generate(track, currentPhase);
        }

        return new MinedObjectSpawnDirective
        {
            minedObjectType = this.minedObjectType,
            role = this.role,
            assignedTrack = track,
            //noteSetSeries = null, // 🔥 no longer needed in procedural system
            trackModifierType = this.trackModifierType,
            displayColor = color,
            minedObjectPrefab = objectRegistry.GetPrefab(minedObjectType, trackModifierType),
            prefab = nodeRegistry.GetPrefab(minedObjectType, trackModifierType),
            remixUtility = remixUtil,
            noteSet = generatedNoteSet // 🎯 Inject the runtime NoteSet
        };
    }


    public override string ToString()
    {
        return $"{role} | {minedObjectType} [{trackModifierType}] | weight: {weight}, quota: {quota}, rarity: {rarityTier}";
    }
}
