using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public static class ShardColorUtility
{
    // 🎨 Base palettes for utility categories
    private static readonly Color MoodShiftColor = new Color(1f, 0.7f, 0.9f);      // pastel rose
    private static readonly Color StructureShiftColor = new Color(0.5f, 0.9f, 1f);  // cyan-teal
    private static readonly Color UnknownColor = Color.gray;
    public static Color ResolveColorFromMineNodePrefab(GameObject mineNodePrefab, InstrumentTrackController controller)
    {
        if (mineNodePrefab == null || controller == null)
        {
            Debug.LogWarning("⚠️ ResolveColorFromMineNodePrefab: Null input.");
            return Color.gray;
        }

        var mineNodeScript = mineNodePrefab.GetComponent<MineNode>();
        if (mineNodeScript == null || mineNodeScript.minedPrefabs.Length == 0)
        {
            Debug.LogWarning("⚠️ MineNode prefab missing script or minedPrefabs.");
            return Color.gray;
        }

        foreach (GameObject minedPrefab in mineNodeScript.minedPrefabs)
        {
            var noteSpawner = minedPrefab.GetComponent<NoteSpawnerMinedObject>();
            if (noteSpawner != null)
            {
                var track = controller.FindTrackByRole(noteSpawner.musicalRole);
                if (track != null) return track.trackColor;
            }

            var utility = minedPrefab.GetComponent<TrackUtilityMinedObject>();
            if (utility != null)
            {
                var track = controller.FindTrackByRole(utility.targetRole);
                if (track != null) return track.trackColor;
            }
        }

        return Color.gray;
    }
    public static Color GetRoleColor(MusicalRole role, InstrumentTrackController controller)
    {
        var track = controller.FindTrackByRole(role);
        return track != null ? track.trackColor : Color.gray;
    }

    public static Color GetShardColor(GameObject minedPrefab, InstrumentTrackController controller)
    {
        if (minedPrefab == null || controller == null)
            return Color.gray;

        // 🎵 Notes
        var noteSpawner = minedPrefab.GetComponent<NoteSpawnerMinedObject>();
        if (noteSpawner != null)
        {
            return GetRoleColor(noteSpawner.musicalRole, controller);
        }

        // 🛠️ Utilities
        var utility = minedPrefab.GetComponent<TrackUtilityMinedObject>();
        if (utility != null)
        {
            return GetRoleColor(utility.targetRole, controller);
        }

        return Color.gray;
    }
    public static Color EstimateShardColor(GameObject prefab)
    {
            if (prefab == null)
            {
                Debug.LogWarning("❌ ShardColorUtility: Reward prefab is null.");
                return Color.gray;
            }

        var noteSpawner = prefab.GetComponent<NoteSpawnerMinedObject>();
        if (noteSpawner != null)
        {
            return RoleColor(noteSpawner.musicalRole);
        }

        var utility = prefab.GetComponent<TrackUtilityMinedObject>();
        if (utility != null)
        {
            switch (utility.type)
            {
                case  TrackModifierType.Drift:
                case  TrackModifierType.Solo:
                case  TrackModifierType.Remix:
                case TrackModifierType.MoodShift:
                    return MoodShiftColor;
                case TrackModifierType.Expansion:
                case TrackModifierType.Contract:
                case TrackModifierType.Magic:
                case TrackModifierType.StructureShift:
                    return StructureShiftColor;
            }
        }

        return UnknownColor;
    }

    private static Color RoleColor(MusicalRole role)
    {
        return role switch
        {
            MusicalRole.Groove => new Color(0.4f, 0.6f, 1f),
            MusicalRole.Harmony => new Color(1f, 0.5f, 0.8f),
            MusicalRole.Bass => new Color(0.7f, 0.5f, 1f),
            MusicalRole.Lead => new Color(1f, 0.8f, 0.3f),
            _ => Color.gray
        };
    }

}

public class PhaseStar : MonoBehaviour
{
    [Header("Star Settings")]
    public int hitsRequired = 8;
    public GameObject diamondVisualPrefab;
    public float hitCooldown = 0.75f;
    public float impactForce = 8f;
    public GameObject impactEffectPrefab;
    public float gravityRampDuration = 3f;
    public float maxGravityForce = 20f;
    public float blackHoleRange = 12f;
    public GameObject collapseEffectPrefab;

    private DrumTrack drumTrack;
    private MineNodeSpawnerSet spawnerSet;
    private MineNodeProgressionManager progressionManager;

    private List<DiamondVisual> diamonds = new();
    private bool isDestroyed = false;
    private bool canBeHit = true;
    private bool isDepleted = false;
    private bool collapseStarted = false;

    private int orbitCount = 0;
    private Collider2D starCollider;

    void Awake()
    {
        starCollider = GetComponent<Collider2D>();
    }

    public void Initialize(
    DrumTrack track,
    MusicalPhase phase,
    MineNodeSpawnerSet set,
    MineNodeProgressionManager manager)
{
    this.drumTrack = track;
    this.spawnerSet = set;
    this.progressionManager = manager;
    progressionManager.pendingNextPhase = false;
    hitsRequired = MusicalPhaseLibrary.Get(phase).hitsRequired;
    
    for (int i = 0; i < hitsRequired; i++)
    {
        GameObject d = Instantiate(diamondVisualPrefab, transform);

        // 🔁 Rotation style
        RotateConstant rotator = d.GetComponent<RotateConstant>();
        if (rotator != null)
        {
            rotator.shardIndex = i;
            rotator.rotationMode = MusicalPhaseLibrary.Get(phase).rotationMode;
            rotator.baseSpeed = MusicalPhaseLibrary.Get(phase).rotationSpeed;
        }

        float angle = (360f / hitsRequired) * i;
        d.transform.localPosition = Vector3.zero;
        d.transform.localRotation = Quaternion.Euler(0, 0, angle);

        // 🎨 Assign color from estimated reward prefab
        DiamondVisual visual = d.GetComponent<DiamondVisual>();
        Color logicColor = MusicalPhaseLibrary.Get(phase).visualColor;
        logicColor.a = 50f;
        
        // ✅ Ensure diamond is registered even if visual is null
        if (visual != null)
        {
            visual.SetAssignedColor(logicColor);
        }
        else
        {
            Debug.LogWarning($"[PhaseStar] DiamondVisual missing on shard {i}");
        }

        diamonds.Add(visual); // could be null, and that's OK — avoids out-of-range errors later
    }
    
    BeginEntrance(); 
}
    
    private void BeginEntrance()
    {
        StartCoroutine(EntranceRoutine());
    }

    private IEnumerator EntranceRoutine()
    {
        // Optional: make non-interactable at first
        var collider = GetComponent<Collider2D>();
        if (collider) collider.enabled = false;

        float duration = 2f;
        float elapsed = 0f;
        Vector3 startScale = Vector3.zero;
        Vector3 endScale = transform.localScale;
        transform.localScale = startScale;

        SpriteRenderer sr = GetComponent<SpriteRenderer>();
        if (sr != null)
        {
            Color c = sr.color;
            c.a = 0f;
            sr.color = c;
        }

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;

            // Scale and fade in
            transform.localScale = Vector3.Lerp(startScale, endScale, Mathf.SmoothStep(0, 1, t));

            if (sr != null)
            {
                Color c = sr.color;
                c.a = Mathf.Lerp(0f, 1f, t);
                sr.color = c;
            }

            yield return null;
        }

        if (collider) collider.enabled = true;
    }

    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (isDepleted) return;

        Vehicle v = coll.gameObject.GetComponent<Vehicle>();
        if (v != null && canBeHit)
        {
            Vector2 contactPoint = coll.GetContact(0).point;
            StartCoroutine(ImpactResponse(v, contactPoint));
        }
    }

    private IEnumerator ImpactResponse(Vehicle vehicle, Vector2 contactPoint)
    {
        canBeHit = false;

        if (impactEffectPrefab != null)
        {
            Instantiate(impactEffectPrefab, contactPoint, Quaternion.identity);
        }

        Vector2 bounceDirection = (vehicle.transform.position - transform.position).normalized;
        vehicle.ApplyImpulse(bounceDirection * impactForce);

        Color shardColor = Color.white;

        if (orbitCount < diamonds.Count && diamonds[orbitCount] != null)
        {
            shardColor = diamonds[orbitCount].assignedColor;
            Destroy(diamonds[orbitCount].gameObject);
            diamonds[orbitCount] = null;
        }

        SpawnColoredMineNode(orbitCount, contactPoint, shardColor);
        orbitCount++;

        if (orbitCount == hitsRequired - 2)
        {
            if (diamonds[hitsRequired - 2] != null) diamonds[hitsRequired - 2].RotateToCross();
            if (diamonds[hitsRequired - 1] != null) diamonds[hitsRequired - 1].RotateToCross();
        }

        if (orbitCount >= hitsRequired && !collapseStarted)
        {
            isDepleted = true;
            collapseStarted = true;
            StartCoroutine(StartCollapseEvent());
        }

        yield return new WaitForSeconds(hitCooldown);
        canBeHit = true;
    }

    private IEnumerator StartCollapseEvent()
    {
        if (starCollider != null)
        {
            starCollider.enabled = false;
        }

        float t = 0f;
        while (drumTrack.GetRemainingMineNodeCount() > 0)
        {
            t += Time.deltaTime / gravityRampDuration;
            float pullStrength = Mathf.Lerp(0f, maxGravityForce, t);

            foreach (var vehicle in FindAllVehiclesInRange(blackHoleRange))
            {
                Vector2 toCenter = (transform.position - vehicle.transform.position);
                float dist = Mathf.Max(toCenter.magnitude, 0.5f);
                Vector2 pull = toCenter.normalized * (pullStrength / dist);
                vehicle.ApplyGravitationalForce(pull);
            }

            yield return null;
        }

        if (collapseEffectPrefab != null)
        {
            Instantiate(collapseEffectPrefab, transform.position, Quaternion.identity);
        }

        if (drumTrack != null)
        {
            drumTrack.FinalizeCurrentPhaseSnapshot();

            if (drumTrack.galaxyVisualizer != null)
            {
                var snapshot = drumTrack.sessionPhases.LastOrDefault();
                if (snapshot != null)
                {
                    drumTrack.galaxyVisualizer.AddSnapshot(snapshot);
                }
            }
        }
        
        if (drumTrack != null && drumTrack.galaxyVisualizer != null)
        {
            var snapshot = drumTrack.BuildCurrentPhaseSnapshot();
        }
        TriggerPhaseAdvance();
    }

    private List<Vehicle> FindAllVehiclesInRange(float maxRange)
    {
        List<Vehicle> nearbyVehicles = new();
        if (GameFlowManager.Instance == null || GameFlowManager.Instance.localPlayers == null) return nearbyVehicles;

        foreach (var player in GameFlowManager.Instance.localPlayers)
        {
            if (player?.playerVehicle == null) continue;

            float dist = Vector2.Distance(transform.position, player.playerVehicle.transform.position);
            if (dist <= maxRange)
            {
                Vehicle v = player.playerVehicle.GetComponent<Vehicle>();
                if (v != null)
                {
                    nearbyVehicles.Add(v);
                }
            }
        }

        return nearbyVehicles;
    }

        private void FixedUpdate()
    {
        if (!isDepleted) return;

        // Once the star has become a black hole, apply passive pull (if desired)
        // If active pull during StartCollapseEvent is sufficient, this can remain empty
    }

    private void OnTriggerEnter2D(Collider2D coll)
    {
        Vehicle v = coll.GetComponent<Vehicle>();
        if (v != null)
        {
            // Do nothing, handled by OnCollision
        }
    }

    private void SpawnColoredMineNode(int index, Vector3 spawnFrom, Color color)

    {
        if (index >= hitsRequired || spawnerSet == null) return;

        GameObject spawnerPrefab = spawnerSet.GetMineNode();
        if (spawnerPrefab == null) return;

        Vector2Int cell = drumTrack.GetRandomAvailableCell();
        if (cell.x == -1) return;

        Vector3 targetPos = drumTrack.GridToWorldPosition(cell);
        GameObject wrapper = Instantiate(spawnerPrefab, spawnFrom, Quaternion.identity);
        MineNodeSpawner spawner = wrapper.GetComponent<MineNodeSpawner>();

        if (spawner == null) return;

        spawner.SetDrumTrack(drumTrack);
        spawner.SpawnNode(cell);

        if (spawner.SpawnedNode != null)
        {
            spawner.SpawnedNode.SetActive(false);
            StartCoroutine(MoveShardToTarget(spawnFrom, targetPos, wrapper, spawner.SpawnedNode, color));
        }
    }

    private IEnumerator MoveShardToTarget(Vector3 start, Vector3 end, GameObject shard, GameObject mineNode, Color fallbackColor)
    {
        float t = 0f;
        float duration = 0.6f;

        // 🌟 Get the shard color from the diamond if available
        Color shardColor = fallbackColor;
        
        // 🎨 Apply color to the moving shard's sprite
        SpriteRenderer shardSprite = shard.GetComponentInChildren<SpriteRenderer>();
        if (shardSprite != null)
        {
            shardSprite.color = shardColor;
        }

        // 🚀 Animate shard toward node
        while (t < 1f)
        {
            t += Time.deltaTime / duration;
            shard.transform.position = Vector3.Lerp(start, end, Mathf.SmoothStep(0f, 1f, t));
            yield return null;
        }

        // 🪐 Activate and color the MineNode
        if (mineNode != null)
        {
            mineNode.transform.position = end;
            mineNode.SetActive(true);

            MineNode mineNodeComp = mineNode.GetComponent<MineNode>();
            if (mineNodeComp != null && mineNodeComp.coreSprite != null)
            {
                // Get spawned prefab
                var mineNodeSpawner = mineNode.GetComponent<MineNodeSpawner>();
                if (mineNodeSpawner != null && mineNodeSpawner.SpawnedNode != null)
                {
                    Color resolvedColor = ShardColorUtility.ResolveColorFromMineNodePrefab(mineNode, drumTrack.trackController);
                    mineNodeComp.LockColor(resolvedColor);

                }
            }

        }

        Destroy(shard);
    }


    private void TriggerPhaseAdvance()
    {
        if (isDestroyed) return;
        isDestroyed = true;
        StartCoroutine(WaitForRemainingNodes());
    }

    private IEnumerator WaitForRemainingNodes()
    {
        
        progressionManager.phaseLocked = true;

        while (drumTrack != null && drumTrack.GetRemainingMineNodeCount() > 0)
        {
            yield return new WaitForSeconds(0.5f);
        }

        progressionManager.isPhaseInProgress = false;

        var nextPhase = progressionManager.PeekNextPhase();
        if (nextPhase.HasValue)
        {
            progressionManager.MoveToNextPhase(specificPhase: nextPhase.Value);
        }
        else
        {
            Debug.LogWarning("[PhaseStar] No next phase available.");
        }

        Destroy(gameObject);
    }
}
