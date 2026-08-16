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
        if (GameFlowManager.VerboseLogging) Debug.Log($"[RingSessionStore] Saved → {path}");
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

    private static PhaseLibrary _migrationLibrary;
    private static PhaseLibrary MigrationLibrary =>
        _migrationLibrary != null ? _migrationLibrary
            : (_migrationLibrary = Resources.Load<PhaseLibrary>("Phase Library"));

    private static List<MotifSnapshot> LoadFromDirectory(string dir)
    {
        var result = new List<MotifSnapshot>();
        if (!Directory.Exists(dir)) return result;

        var library = MigrationLibrary;

        foreach (var file in Directory.GetFiles(dir, "ring_*.json"))
        {
            try
            {
                var snap = JsonUtility.FromJson<MotifSnapshot>(File.ReadAllText(file));

                // One-time migration: legacy rings predate PhaseId. Backfill it from the
                // *current* PhaseLibrary while PhaseIndex is still trustworthy (i.e. before
                // any reorder happens relative to when this ring was saved).
                if (string.IsNullOrEmpty(snap.PhaseId) && library?.phases != null
                    && snap.PhaseIndex >= 0 && snap.PhaseIndex < library.phases.Count)
                {
                    var resolvedId = library.phases[snap.PhaseIndex]?.phaseId;
                    if (!string.IsNullOrEmpty(resolvedId))
                    {
                        snap.PhaseId = resolvedId;
                        File.WriteAllText(file, JsonUtility.ToJson(snap, true));
                        if (GameFlowManager.VerboseLogging)
                            Debug.Log($"[RingSessionStore] Migrated {file}: backfilled PhaseId={resolvedId}");
                    }
                }

                result.Add(snap);
            }
            catch (Exception e) { Debug.LogWarning($"[RingSessionStore] Failed to load {file}: {e.Message}"); }
        }
        if (GameFlowManager.VerboseLogging) Debug.Log($"[RingSessionStore] Loaded {result.Count} rings from {dir}");
        return result;
    }
}
