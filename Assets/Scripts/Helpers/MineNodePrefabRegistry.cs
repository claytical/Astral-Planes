using System;
using System.Collections.Generic;
using UnityEngine;
[Serializable]
public struct Entry
{
    public MinedObjectType type;
    public TrackModifierType modifier;
    public GameObject prefab;
}

[CreateAssetMenu(fileName = "MineNodePrefabRegistry", menuName = "Astral Planes/Mine Node Prefab Registry")]
public class MineNodePrefabRegistry : ScriptableObject
{

    [SerializeField] private List<Entry> entries = new();

    private Dictionary<(MinedObjectType, TrackModifierType), GameObject> map;

    private void OnEnable()
    {
        map = new();
        foreach (var entry in entries)
        {
            var key = (entry.type, entry.modifier);
            if (!map.ContainsKey(key))
                map[key] = entry.prefab;
        }
    }

    public GameObject GetPrefab(MinedObjectType type, TrackModifierType modifier)
    {
        var key = (type, modifier);
        if (map != null && map.TryGetValue(key, out var prefab))
            return prefab;

        Debug.LogWarning($"⚠️ No prefab registered for {type} + {modifier}");
        return null;
    }
}