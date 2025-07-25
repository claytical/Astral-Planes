using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CosmicDustGenerator : MonoBehaviour
{
    public DrumTrack drumTrack;
    public GameObject hexagonShieldPrefab;
    public int radius = 6; // How far from center to generate
    public int iterations = 3;
    private List<GameObject> hexagons = new List<GameObject>();
    private Dictionary<Vector2Int, bool> fillMap = new();
    private Dictionary<Vector2Int, GameObject> hexMap = new(); // Position → Hex
    private Dictionary<Vector2Int, Coroutine> regrowthCoroutines = new();
    private List<(Vector2Int grid, Vector3 pos)> pendingSpawns = new();
    private Coroutine spawnRoutine;
    private bool isToxic = false;
    private bool IsWorldPositionInsideScreen(Vector3 worldPos)
    {
        Vector3 viewport = Camera.main.WorldToViewportPoint(worldPos);
        return viewport.x >= 0f && viewport.x <= 1f && viewport.y >= 0f && viewport.y <= 1f;
    }

    public List<(Vector2Int, Vector3)> CalculateMazeGrowth(Vector2Int center, MusicalPhase phase, float hollowRadius = 2.5f)
    {
        List<(Vector2Int, Vector3)> growthInstructions = new();
        fillMap.Clear();

        Vector3 centerWorld = drumTrack.GridToWorldPosition(center);
        float fillChance = GetFillProbability(phase);
        int gridWidth = drumTrack.GetSpawnGridWidth();
        int gridHeight = drumTrack.GetSpawnGridHeight();

        for (int x = 0; x < gridWidth; x++)
        {
            for (int y = 0; y < gridHeight; y++)
            {
                Vector2Int pos = new(x, y);
                if (!drumTrack.IsSpawnCellAvailable(x, y)) continue;

                Vector3 cellWorld = drumTrack.GridToWorldPosition(pos);
                float distanceToCenter = Vector3.Distance(cellWorld, centerWorld);
                if (distanceToCenter < hollowRadius) continue;

                fillMap[pos] = Random.value < fillChance;
            }
        }

        // Run CA iterations
        for (int i = 0; i < iterations; i++)
        {
            Dictionary<Vector2Int, bool> next = new();

            foreach (var cell in fillMap.Keys)
            {
                int neighbors = CountFilledNeighbors(cell);
                bool current = fillMap[cell];

                if (current && (neighbors < 2 || neighbors > 4))
                    next[cell] = false;
                else if (!current && neighbors == 3)
                    next[cell] = true;
                else
                    next[cell] = current;
            }

            fillMap = next;
        }

        // Prepare spawn list
        foreach (var kv in fillMap)
        {
            if (!kv.Value) continue;

            Vector2Int gridPos = kv.Key;
            Vector3 worldPos = drumTrack.GridToWorldPosition(gridPos);
            if (!IsWorldPositionInsideScreen(worldPos)) continue;

            growthInstructions.Add((gridPos, worldPos));
        }

        return growthInstructions;
    }

    public void BeginStaggeredMazeRegrowth(List<(Vector2Int, Vector3)> cellsToGrow)
    {
        if (spawnRoutine != null)
            StopCoroutine(spawnRoutine);

        spawnRoutine = StartCoroutine(StaggeredGrowth(cellsToGrow));
    }

    private IEnumerator StaggeredGrowth(List<(Vector2Int, Vector3)> cells)
    {
        foreach (var (grid, pos) in cells)
        {
            GameObject hex = Instantiate(hexagonShieldPrefab, pos, Quaternion.identity);
          
            drumTrack.OccupySpawnGridCell(grid.x, grid.y, GridObjectType.Node);
            hexagons.Add(hex);
            CosmicDust hexScript = hex.GetComponent<CosmicDust>();
            if (hexScript != null)
            {
                if (isToxic)
                {
                    hexScript.SwitchType(CosmicDust.CosmicDustType.Depleting);
                }
                else
                {
                    hexScript.SwitchType(CosmicDust.CosmicDustType.Friendly);
                }
                hexScript.SetPhaseColor(drumTrack.currentPhase);
                hexScript.SetDrumTrack(drumTrack); 
                hexScript.Begin();
            }

            RegisterHex(grid, hex);
            yield return new WaitForSeconds(UnityEngine.Random.Range(2f, drumTrack.currentStep));
        }
    }
    public IEnumerator BreakSelfThenNeighbors(CosmicDust origin, SoundEffectMood mood, Vector2Int center, int radius, float delay)
    {
        Debug.Log($"Breaking Self Then Neigbhors");

        if (origin != null)
        {
            Debug.Log($"Breaking hexagon");
            origin.BreakHexagon(mood); // Central hex breaks first
        }

        yield return new WaitForSeconds(delay);
        Debug.Log($"Breaking Self Then Neigbhors, Continued");

        yield return BreakNearbyHexesSequentially(mood, center, radius, delay);
    }

    public IEnumerator BreakNearbyHexesSequentially(SoundEffectMood mood, Vector2Int center, int radius, float delay)
    {
        List<Vector2Int> targets = new();

        foreach (var kvp in hexMap)
        {
            float dist = Vector2Int.Distance(center, kvp.Key);
            if (dist <= radius)
            {
                targets.Add(kvp.Key);
            }
        }

        // Optional: sort by distance to center for a radial ripple
        targets.Sort((a, b) =>
            Vector2Int.Distance(center, a).CompareTo(Vector2Int.Distance(center, b)));

        foreach (var pos in targets)
        {
            if (hexMap.TryGetValue(pos, out GameObject hex) && hex != null)
            {
                if (hex.TryGetComponent<Explode>(out var explode))
                {
                    CollectionSoundManager.Instance.PlayEffect(SoundEffectPreset.Dust);
                    explode.Permanent();
                }
                else
                {
                    Destroy(hex);
                }

                drumTrack.FreeSpawnCell(pos.x, pos.y);
                RemoveHex(pos);
            }

            yield return new WaitForSeconds(delay);
        }
    }

    private void RegisterHex(Vector2Int gridPos, GameObject hex)
    {
        hexMap[gridPos] = hex;
    }

    public void RemoveHex(Vector2Int gridPos)
    {
        hexMap.Remove(gridPos);
    }
    private IEnumerator RegrowHexagon(Vector2Int gridPos, MusicalPhase phase)
    {
        float delay = phase switch
        {
            MusicalPhase.Establish => 8f,
            MusicalPhase.Evolve => 6f,
            MusicalPhase.Intensify => 3f,
            MusicalPhase.Release => 5f,
            MusicalPhase.Wildcard => 2.5f,
            MusicalPhase.Pop => 2f,
            _ => 4f
        };

        yield return new WaitForSeconds(delay);

        if (hexMap.ContainsKey(gridPos)) yield break; // Already regrown

        Vector3 worldPos = drumTrack.GridToWorldPosition(gridPos);
        GameObject hex = Instantiate(hexagonShieldPrefab, worldPos, Quaternion.identity);
        hexagons.Add(hex);
        drumTrack.OccupySpawnGridCell(gridPos.x, gridPos.y, GridObjectType.Node);

        if (hex.TryGetComponent<CosmicDust>(out var shield))
        {
            shield.SetPhaseColor(phase);
            shield.SetDrumTrack(drumTrack);
            shield.Begin();
        }

        hexMap[gridPos] = hex;
        regrowthCoroutines.Remove(gridPos);
    }
public void GenerateDust(Vector2Int center, MusicalPhase phase, float hollowRadius = 2.5f, bool toxic = false)
    {
        isToxic = toxic;
        fillMap.Clear();
        Vector3 centerWorld = drumTrack.GridToWorldPosition(center);
        float fillChance = GetFillProbability(phase);
        int gridWidth = drumTrack.GetSpawnGridWidth();
        int gridHeight = drumTrack.GetSpawnGridHeight();

        for (int x = 0; x < gridWidth; x++)
        {
            for (int y = 0; y < gridHeight; y++)
            {
                Vector2Int pos = new(x, y);
                if (!drumTrack.IsSpawnCellAvailable(x, y)) continue;

                // Skip area near PhaseStar
                Vector3 cellWorld = drumTrack.GridToWorldPosition(pos);
                float distanceToCenter = Vector3.Distance(cellWorld, centerWorld);
                if (distanceToCenter < hollowRadius) continue;

                fillMap[pos] = Random.value < fillChance;
            }
        }


        // Run CA iterations
        for (int i = 0; i < iterations; i++)
        {
            Dictionary<Vector2Int, bool> next = new();

            foreach (var cell in fillMap.Keys)
            {
                int neighbors = CountFilledNeighbors(cell);
                bool current = fillMap[cell];

                if (current && (neighbors < 2 || neighbors > 4))
                    next[cell] = false;
                else if (!current && neighbors == 3)
                    next[cell] = true;
                else
                    next[cell] = current;
            }

            fillMap = next;
        }

// Spawn hex shields
        foreach (var kv in fillMap)
        {
            if (!kv.Value) continue;

            Vector2Int gridPos = kv.Key;

            Vector2Int relativeGridPos = gridPos - center;
            if (!IsHexInsideScreen(relativeGridPos, drumTrack.GetGridCellSize())) continue;

            Vector3 relativePos = drumTrack.HexToWorldPosition(relativeGridPos, drumTrack.GetGridCellSize());
            Vector3 worldPos = centerWorld + relativePos;

            pendingSpawns.Add((gridPos, worldPos));
        }
        if (spawnRoutine != null) StopCoroutine(spawnRoutine);
        spawnRoutine = StartCoroutine(StaggeredGrowth(pendingSpawns));

    }
   
    public void TriggerRegrowth(Vector2Int gridPos, MusicalPhase phase)
    {
        if (regrowthCoroutines.ContainsKey(gridPos)) return;
        
        regrowthCoroutines[gridPos] = StartCoroutine(RegrowHexagon(gridPos, phase));
    }

    private bool IsHexInsideScreen(Vector2Int gridPos, float cellSize, float bottomY = -4.25f, float topY = 4.25f)
    {
        float height = Mathf.Sqrt(3f) / 2f * cellSize;
        float y = gridPos.y * height + (gridPos.x % 2 == 1 ? height / 2f : 0f);
        return y >= bottomY && y <= topY;
    }

    private int CountFilledNeighbors(Vector2Int cell)
    {
        int count = 0;
        foreach (var dir in GetHexDirections(cell.y))
        {
            Vector2Int neighbor = cell + dir;
            if (fillMap.TryGetValue(neighbor, out bool filled) && filled)
                count++;
        }
        return count;
    }

    private List<Vector2Int> GetHexDirections(int row)
    {
        // Even-q offset coordinates
        return row % 2 == 0 ? new List<Vector2Int>
        {
            new(1, 0), new(0, 1), new(-1, 1),
            new(-1, 0), new(-1, -1), new(0, -1)
        } : new List<Vector2Int>
        {
            new(1, 0), new(1, 1), new(0, 1),
            new(-1, 0), new(0, -1), new(1, -1)
        };
    }

    private float GetFillProbability(MusicalPhase phase)
    {
        return phase switch
        {
            MusicalPhase.Establish => 0.20f,
            MusicalPhase.Evolve => 0.30f,
            MusicalPhase.Intensify => 0.45f,
            MusicalPhase.Release => 0.15f,
            MusicalPhase.Wildcard => 0.40f + Random.Range(-0.1f, 0.1f),
            _ => 0.35f
        };
    }

    public void ClearMaze()
    {
        foreach (var t in hexagons)
        {
            if(t == null) continue;
            Explode explode = t.GetComponent<Explode>();
            if (explode != null)
            {
                explode.Permanent();
            }
            Vector2Int gridPos = drumTrack.WorldToGridPosition(t.transform.position);
            drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);
        }
        drumTrack.activeHexagons.Clear();

    }
}
