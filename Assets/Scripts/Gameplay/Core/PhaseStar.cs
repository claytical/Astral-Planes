using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

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
    public static Color RoleColor(MusicalRole role)
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
    public ParticleSystem particleSystem;
    public float pulseSpeed = 1f;
    public float minAlpha = .1f;
    public float maxAlpha = 1f;

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
    private ParticleSystem.MainModule starParticlesMain;
    private MusicalPhase assignedPhase;
    void Awake()
    {
        starCollider = GetComponent<Collider2D>();
        starParticlesMain = particleSystem.main;
    }

    public void Initialize(DrumTrack track, MusicalPhase phase, MineNodeSpawnerSet set, MineNodeProgressionManager manager)
    {
        assignedPhase = phase;
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

        progressionManager.phaseStarActive = true;

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
          //  MusicalPhase? next = progressionManager.PeekNextPhase(); // Or hardcoded if needed
            MusicalPhaseProfile profile = progressionManager.GetProfileForPhase(assignedPhase);
            foreach (var hex in FindObjectsOfType<HexagonShield>())
            {
                hex.ShiftToPhaseColor(profile,2f);
            }

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
            RemixTracksForDarkStar();
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
        private void RemixTracksForDarkStar()
    {
        foreach (var track in drumTrack.trackController.tracks)
        {
            if (track.GetNoteDensity() <= 1) continue;

            var set = track.GetCurrentNoteSet();
            if (set == null) continue;

            set.rhythmStyle = RhythmStyle.Dense;
            set.noteBehavior = NoteBehavior.Lead;

            set.Initialize(track.GetTotalSteps());
            track.ClearLoopedNotes(TrackClearType.Remix);

            var steps = set.GetStepList();
            var notes = set.GetNoteList();

            for (int i = 0; i < Mathf.Min(4, steps.Count); i++)
            {
                int step = steps[i];
                int note = set.GetNextArpeggiatedNote(step);
                int dur = track.CalculateNoteDuration(step, set);
                float vel = Random.Range(60f, 100f);
                track.GetPersistentLoopNotes().Add((step, note, dur, vel));
            }
            
        }
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

    private void Update()
    {
        float alpha = Mathf.Lerp(minAlpha, maxAlpha, 0.5f + 0.5f * Mathf.Sin(Time.time * pulseSpeed));

        Color startColor = starParticlesMain.startColor.color;
        startColor.a = alpha;
        starParticlesMain.startColor = startColor;

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

        private void GlitchRemix()
    {
        foreach (var track in drumTrack.trackController.tracks)
        {
            if (track.GetNoteDensity() == 0) continue;

            NoteSet set = track.GetCurrentNoteSet();
            if (set == null) continue;

            // Random mutation: force Lead to syncopate, Groove to jitter, Harmony to invert
            switch (track.assignedRole)
            {
                case MusicalRole.Lead:
                    set.rhythmStyle = RhythmStyle.Syncopated;
                    break;
                case MusicalRole.Groove:
                    set.rhythmStyle = RhythmStyle.FourOnTheFloor;
                    break;
                case MusicalRole.Harmony:
                    set.chordPattern = ChordPattern.Arpeggiated;
                    break;
                case MusicalRole.Bass:
                    set.noteBehavior = NoteBehavior.Drone;
                    break;
            }

            set.Initialize(track.GetTotalSteps());
            track.ClearLoopedNotes(TrackClearType.Remix);

            var steps = set.GetStepList();
            var notes = set.GetNoteList();

            for (int i = 0; i < Mathf.Min(4, steps.Count); i++)
            {
                int step = steps[i];
                int note = set.GetNextArpeggiatedNote(step);
                int dur = track.CalculateNoteDuration(step, set);
                float vel = Random.Range(60f, 90f);
                track.GetPersistentLoopNotes().Add((step, note, dur, vel));
            }
        }

    }
        private IEnumerator WaitForRemainingNodes()
    {
        progressionManager.phaseLocked = true;

        while (drumTrack != null && drumTrack.GetRemainingMineNodeCount() > 0)
        {
            yield return new WaitForSeconds(0.5f);
        }

        Debug.Log("✅ Mine nodes cleared. Checking darkStarModeEnabled...");
        if (drumTrack.darkStarModeEnabled)
        {
            Debug.Log("Glitch Remix Queued");
            GlitchRemix();
            float loopLength = drumTrack.GetLoopLengthInSeconds();
            Debug.Log($"🕒 Waiting {loopLength} seconds before Dark Star spawns...");
            yield return new WaitForSeconds(loopLength); // ⏳ Give breathing room
            Debug.Log("🌑 Spawning DarkStar now.");
            progressionManager.SpawnDarkStar(transform.position);
        }
        else
        {
            // Skip DarkStar and trigger phase transition immediately
            Debug.Log("🚀 Calling OnDarkStarComplete()");
            RemixTracksAfterDarkStar();
            progressionManager.SetDarkStarSpawnPoint(transform.position);
            progressionManager.phaseStarActive = false;
            progressionManager.OnDarkStarComplete();
        }

        Destroy(gameObject);
        drumTrack.isPhaseStarActive = true;
    }

        private void RemixTracksAfterDarkStar()
    {
        foreach (var track in drumTrack.trackController.tracks)
        {
            var set = track.GetCurrentNoteSet();
            if (set == null) continue;

            // 🎛 Reset to default behavior for the role
            var profile = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
            if (profile == null) continue;

            set.noteBehavior = profile.defaultBehavior;
            set.rhythmStyle = RhythmStyle.Sparse;
            set.chordPattern = ChordPattern.RootTriad;
            set.Initialize(track.GetTotalSteps());

            track.ClearLoopedNotes(TrackClearType.Remix);

            var steps = set.GetStepList();
            var notes = set.GetNoteList();

            for (int i = 0; i < Mathf.Min(6, steps.Count); i++)
            {
                int step = steps[i];
                int note = set.GetNextArpeggiatedNote(step);
                int dur = track.CalculateNoteDuration(step, set);
                float vel = Random.Range(50f, 90f);
                track.GetPersistentLoopNotes().Add((step, note, dur, vel));
            }
        }

        drumTrack.trackController.UpdateVisualizer();
    }

        private void TriggerPhaseAdvance()
    {
        if (isDestroyed) return;
        isDestroyed = true;
        Debug.Log("📡 PhaseStar.TriggerPhaseAdvance()");

        if (progressionManager == null)
        {
            Debug.LogWarning("⚠️ PhaseStar has no progressionManager reference.");
            return;
        }
        StartCoroutine(WaitForRemainingNodes());
    }
}
