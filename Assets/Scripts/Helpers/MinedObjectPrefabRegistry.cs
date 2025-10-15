using System;
using System.Collections.Generic;
using System.Linq;
using Gameplay.Mining;
using UnityEngine;

[Serializable]
[CreateAssetMenu(fileName = "MinedObjectPrefabRegistry", menuName = "Astral Planes/Mined Object Prefab Registry")]
public class MinedObjectPrefabRegistry : ScriptableObject
{
    [Serializable]
    private struct Entry
    {
        public MinedObjectType type;
        public TrackModifierType modifier;
        public GameObject prefab;
    }

    [Header("Registry Entries (Spawner + Utilities)")]
    [SerializeField] private List<Entry> entries = new();

    // runtime maps
    private GameObject spawnerPrefab;                              // single source of truth for spawners
    private Dictionary<TrackModifierType, GameObject> utilMap;     // utilities by modifier

    private void OnEnable()  { BuildMaps(); }
#if UNITY_EDITOR
    private void OnValidate(){ BuildMaps(); }
#endif

    private void BuildMaps()
    {
        utilMap = new Dictionary<TrackModifierType, GameObject>();
        spawnerPrefab = null;


        // 2) Scan entries; fill spawner if not set; fill utility map
        foreach (var e in entries)
        {
            if (e.prefab == null) continue;

            if (e.type == MinedObjectType.NoteSpawner)
            {
                if (spawnerPrefab == null)
                {
                    if (HasComponent<NoteSpawnerMinedObject>(e.prefab))
                        spawnerPrefab = e.prefab;
                    else
                        Debug.LogError($"❌ Expected NoteSpawnerMinedObject on prefab for NoteSpawner, but not found: {e.prefab.name}");
                }
                // ignore modifier for spawner entries; first valid wins
                continue;
            }

            // TrackUtility (or other future types): index by modifier
            if (HasComponent<TrackUtilityMinedObject>(e.prefab))
            {
                if (!utilMap.ContainsKey(e.modifier))
                    utilMap[e.modifier] = e.prefab;
            }
            else
            {
                Debug.LogError($"❌ Expected TrackUtilityMinedObject on prefab for {e.type}+{e.modifier}, but not found: {e.prefab.name}");
            }
        }

        if (spawnerPrefab == null)
            Debug.LogError("❌ No valid NoteSpawner prefab found in registry (and no override assigned).");
    }

    // ---- Public API ---------------------------------------------------------

    /// <summary>Single canonical NoteSpawner payload prefab.</summary>
    public GameObject GetSpawnerPrefab()
    {
        if (!spawnerPrefab)
            Debug.LogError("❌ NoteSpawner prefab is null. Assign a Spawner override or add a NoteSpawner entry.");
        return spawnerPrefab;
    }

    /// <summary>
    /// Convenience: choose prefab from a directive.
    /// - For NoteSpawner: returns the canonical spawner prefab (ignores modifier).
    /// - For TrackUtility: returns prefab by modifier.
    /// </summary>
    public GameObject GetPrefab(MinedObjectSpawnDirective directive)
    {
        if (directive == null) return null;
        return directive.minedObjectType == MinedObjectType.NoteSpawner
            ? GetSpawnerPrefab()
            : GetPrefab(directive.minedObjectType, directive.trackModifierType);
    }

    /// <summary>
    /// Back-compat: for NoteSpawner returns the canonical spawner prefab (modifier ignored).
    /// For utilities returns prefab by modifier.
    /// </summary>
    public GameObject GetPrefab(MinedObjectType type, TrackModifierType modifier)
    {
        if (type == MinedObjectType.NoteSpawner)
            return GetSpawnerPrefab();

        if (utilMap != null && utilMap.TryGetValue(modifier, out var p))
        {
            if (p == null)
            {
                Debug.LogWarning($"⚠️ Prefab for {type}+{modifier} is null.");
                return null;
            }
            return p;
        }

        Debug.LogWarning($"⚠️ No MinedObject prefab registered for {type} + {modifier}");
        return null;
    }

    // ---- Utils --------------------------------------------------------------

    private static bool HasComponent<T>(GameObject go) where T : Component =>
        go && go.GetComponent<T>() != null;
}
