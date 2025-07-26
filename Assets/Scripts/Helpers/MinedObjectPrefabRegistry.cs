using System;
using System.Collections.Generic;
using Gameplay.Mining;
using UnityEngine;
[Serializable]

[CreateAssetMenu(fileName = "MinedObjectPrefabRegistry", menuName = "Astral Planes/Mined Object Prefab Registry")]
public class MinedObjectPrefabRegistry : ScriptableObject
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
        {
            if (prefab == null)
            {
                Debug.LogWarning($"⚠️ Prefab for {key} is null.");
                return null;
            }

            var hasNoteSpawner = prefab.GetComponent<NoteSpawnerMinedObject>() != null;
            var hasUtility     = prefab.GetComponent<TrackUtilityMinedObject>() != null;

            if (type == MinedObjectType.NoteSpawner && !hasNoteSpawner)
            {
                Debug.LogError($"❌ Expected NoteSpawnerMinedObject on prefab for {key}, but not found.");
                return null;
            }

            if (type != MinedObjectType.NoteSpawner && !hasUtility)
            {
                Debug.LogError($"❌ Expected TrackUtilityMinedObject on prefab for {key}, but not found.");
                return null;
            }

            return prefab;
        }

        Debug.LogWarning($"⚠️ No MinedObject prefab registered for {type} + {modifier}");
        return null;
    }

}