using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Persists one MotifSnapshot per (phase, motif) position.
/// File naming mirrors PhaseLibrary: ring_{phaseIndex}_{motifIndex}.json.
/// Each bridge completion overwrites the previous rendition for that position.
/// </summary>
public static class RingSessionStore
{
    private static string SaveDirectory =>
        Application.persistentDataPath + "/RingSessions/";

    private static string FilePath(int phaseIndex, int motifIndex) =>
        Path.Combine(SaveDirectory, $"ring_{phaseIndex}_{motifIndex}.json");

    public static void SaveRingToDisk(MotifSnapshot snap)
    {
        if (!Directory.Exists(SaveDirectory))
            Directory.CreateDirectory(SaveDirectory);

        string path = FilePath(snap.PhaseIndex, snap.MotifIndex);
        File.WriteAllText(path, JsonUtility.ToJson(snap, true));
        Debug.Log($"[RingSessionStore] Saved → {path}");
    }

    public static List<MotifSnapshot> LoadAllRingsFromDisk()
    {
#if UNITY_EDITOR
        string mockDir = Path.Combine(Application.streamingAssetsPath, "RingSessions");
        if (Directory.Exists(mockDir) && Directory.GetFiles(mockDir, "ring_*.json").Length > 0)
            return LoadFromDirectory(mockDir);
#endif
        return LoadFromDirectory(SaveDirectory);
    }

    private static List<MotifSnapshot> LoadFromDirectory(string dir)
    {
        var result = new List<MotifSnapshot>();
        if (!Directory.Exists(dir)) return result;

        foreach (var file in Directory.GetFiles(dir, "ring_*.json"))
        {
            try { result.Add(JsonUtility.FromJson<MotifSnapshot>(File.ReadAllText(file))); }
            catch (Exception e) { Debug.LogWarning($"[RingSessionStore] Failed to load {file}: {e.Message}"); }
        }
        Debug.Log($"[RingSessionStore] Loaded {result.Count} rings from {dir}");
        return result;
    }
}
