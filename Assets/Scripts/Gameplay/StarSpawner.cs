// Finalized StarSpawner.cs linked to DrumTrack logic
using System.Collections.Generic;
using UnityEngine;

public class StarSpawner : MonoBehaviour
{
    [System.Serializable]
    public class StarSpawnProfile
    {
        public SpawnerPhase phase;
        public float spawnInterval = 6f;
        public float timeToLive = 8f;
        public int maxStarsOnScreen = 1;
        public bool useDynamicInterval = false;
    }

    [Header("References")]
    public DrumTrack drumTrack;
    public GameObject starPrefab;

    [Header("Phase-Based Star Profiles")]
    public List<StarSpawnProfile> profiles = new();

    private float spawnTimer = 0f;
    private Dictionary<SpawnerPhase, StarSpawnProfile> profileLookup = new();

    void Start()
    {
        foreach (var profile in profiles)
        {
            if (!profileLookup.ContainsKey(profile.phase))
            {
                Debug.Log($"[StarSpawner] Adding profile for {profile.phase}");
                profileLookup.Add(profile.phase, profile);
            }
        }

        drumTrack.CurrentPattern = DrumLoopPattern.Establish;

    }

    void Update()
    {
// Wait until (loopLength + loopLength / 2) before allowing star spawns
        double elapsedSinceStart = AudioSettings.dspTime - drumTrack.startDspTime;
        float loopDelay = drumTrack.GetLoopLengthInSeconds() * 1.5f;
        if (elapsedSinceStart < loopDelay)
        {
            return;
        }


        SpawnerPhase phase = (SpawnerPhase)drumTrack.CurrentPattern;
        if (!profileLookup.TryGetValue(phase, out var profile))
            return;

        spawnTimer += Time.deltaTime;
        float intervalToUse = profile.useDynamicInterval
            ? GetDynamicInterval(profile)
            : profile.spawnInterval * drumTrack.GetLoopLengthInSeconds();
        
        int starCount = drumTrack.GetCollectedStarCount();

        if (spawnTimer >= intervalToUse && starCount < profile.maxStarsOnScreen)
        {
            SpawnStar(profile.timeToLive);
            spawnTimer = 0f;
        }
    }

    float GetDynamicInterval(StarSpawnProfile profile)
    {
        int noteCount = drumTrack.GetNoteDensityAcrossAllTracks();
        float adjustment = Mathf.Clamp01(noteCount / 24f);
        return Mathf.Lerp(profile.spawnInterval * 1.5f * drumTrack.GetLoopLengthInSeconds(), profile.spawnInterval * 0.5f * drumTrack.GetLoopLengthInSeconds(), adjustment);
    }

    void SpawnStar(float ttl)
    {
        Vector2Int gridPos = drumTrack.GetRandomAvailableCell();
        if (gridPos.x == -1)
        {
            Debug.LogWarning("[StarSpawner] No available grid cell to spawn star.");
            return;
        }

        Vector3 spawnPos = drumTrack.GridToWorldPosition(gridPos);
        GameObject star = Instantiate(starPrefab, spawnPos, Quaternion.identity);

        DrumLoopCollectable starScript = star.GetComponent<DrumLoopCollectable>();
        if (starScript != null)
        {
            starScript.SetDrums(drumTrack);
            starScript.pattern = DrumLoopPattern.Establish;
            starScript.timeToLive = ttl;
            drumTrack.RegisterDrumLoopCollectable(starScript);
        }
        else
        {
            Debug.LogWarning("[StarSpawner] Star prefab missing DrumLoopCollectable component.");
        }
    }
}
