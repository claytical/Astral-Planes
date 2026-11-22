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
    // Bump to wipe every ring saved by an older build (each ring carries its own stamp, so this
    // survives Steam Cloud syncing individual files). 0 = rings saved before versioning existed.
    //
    // Steam Auto-Cloud (partner site, app 3598920) syncs RingSessions/ring_*.json from the
    // WinAppDataLocalLow root, with Mac/Linux root overrides. Keep the folder name, file pattern
    // and company/product names stable, or cloud saves stop matching. Writes go through a .tmp
    // file (not matched by the pattern) so a crash mid-write never uploads a truncated ring.
    public const int CurrentSaveVersion = 1;

    private static string SaveDirectory =>
        Application.persistentDataPath + "/RingSessions/";

    private static string FilePath(int phaseIndex, int motifIndex) =>
        Path.Combine(SaveDirectory, $"ring_{phaseIndex}_{motifIndex}.json");

    public static void SaveRingToDisk(MotifSnapshot snap)
    {
        if (!Directory.Exists(SaveDirectory))
            Directory.CreateDirectory(SaveDirectory);

        snap.SaveVersion = CurrentSaveVersion;
        snap.SavedAtUtcTicks = DateTime.UtcNow.Ticks;
        string path = FilePath(snap.PhaseIndex, snap.MotifIndex);
        WriteAtomic(path, JsonUtility.ToJson(snap, true));
        if (GameFlowManager.VerboseLogging) Debug.Log($"[RingSessionStore] Saved → {path}");
    }

    // Write to a sibling .tmp, then swap it in, so the cloud-synced ring_*.json is never partial.
    private static void WriteAtomic(string path, string contents)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    public static List<MotifSnapshot> LoadAllRingsFromDisk()
    {
#if UNITY_EDITOR
        string mockDir = Path.Combine(Application.streamingAssetsPath, "RingSessions");
        if (Directory.Exists(mockDir) && Directory.GetFiles(mockDir, "ring_*.json").Length > 0)
            return LoadFromDirectory(mockDir, enforceVersion: false); // never delete mock rings
#endif
        return LoadFromDirectory(SaveDirectory, enforceVersion: true);
    }

    /// <summary>Deletes every saved ring (Settings → Reset All Progress). Deletes files, not the
    /// folder, so Steam Auto-Cloud sees per-file deletions and propagates them on clean exit.</summary>
    public static void DeleteAll()
    {
        if (Directory.Exists(SaveDirectory))
        {
            foreach (var file in Directory.GetFiles(SaveDirectory, "ring_*.json")) File.Delete(file);
            foreach (var file in Directory.GetFiles(SaveDirectory, "ring_*.json.tmp")) File.Delete(file);
        }
        Debug.Log($"[RingSessionStore] Deleted all rings in {SaveDirectory}");
    }

    private static PhaseLibrary _migrationLibrary;
    private static PhaseLibrary MigrationLibrary =>
        _migrationLibrary != null ? _migrationLibrary
            : (_migrationLibrary = Resources.Load<PhaseLibrary>("Phase Library"));

    // A ring whose (PhaseId, MotifId) no longer exists in the library (its phase was split or
    // renamed) is re-homed in memory to the first slot, in phase order, that holds its MotifId. Rings that
    // still resolve, or match nothing, are left alone.
    public static bool RehomeOrphan(MotifSnapshot snap, PhaseLibrary library)
    {
        var phases = library?.phases;
        if (phases == null || string.IsNullOrEmpty(snap.PhaseId) || string.IsNullOrEmpty(snap.MotifId)) return false;

        foreach (var ph in phases)
            if (ph?.phaseId == snap.PhaseId && ph.motifs != null && ph.motifs.Exists(m => m != null && m.motifId == snap.MotifId))
                return false;

        for (int p = 0; p < phases.Count; p++)
        {
            var motifs = phases[p]?.motifs;
            if (motifs == null) continue;
            int m = motifs.FindIndex(x => x != null && x.motifId == snap.MotifId);
            if (m < 0) continue;

            snap.PhaseId    = phases[p].phaseId;
            snap.PhaseIndex = p;
            snap.MotifIndex = m;
            return true;
        }
        return false;
    }

    private static List<MotifSnapshot> LoadFromDirectory(string dir, bool enforceVersion)
    {
        var result = new List<MotifSnapshot>();
        if (!Directory.Exists(dir)) return result;

        var library = MigrationLibrary;

        foreach (var file in Directory.GetFiles(dir, "ring_*.json"))
        {
            try
            {
                var snap = JsonUtility.FromJson<MotifSnapshot>(File.ReadAllText(file));
                if (snap == null) continue;

                // Migration: rings from an older save format are wiped, not converted.
                if (enforceVersion && snap.SaveVersion < CurrentSaveVersion)
                {
                    File.Delete(file);
                    Debug.Log($"[RingSessionStore] Discarded v{snap.SaveVersion} save (current v{CurrentSaveVersion}): {file}");
                    continue;
                }

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
                        WriteAtomic(file, JsonUtility.ToJson(snap, true));
                        if (GameFlowManager.VerboseLogging)
                            Debug.Log($"[RingSessionStore] Migrated {file}: backfilled PhaseId={resolvedId}");
                    }
                }

                // In memory only: files are named by slot index, so rewriting under the old name
                // would leave a stale file, and moving it could overwrite another phase's ring.
                if (RehomeOrphan(snap, library) && GameFlowManager.VerboseLogging)
                    Debug.Log($"[RingSessionStore] Re-homed {file} to PhaseId={snap.PhaseId} MotifIndex={snap.MotifIndex}");

                if (snap.SavedAtUtcTicks == 0) snap.SavedAtUtcTicks = File.GetLastWriteTimeUtc(file).Ticks;

                result.Add(snap);
            }
            catch (Exception e) { Debug.LogWarning($"[RingSessionStore] Failed to load {file}: {e.Message}"); }
        }
        if (GameFlowManager.VerboseLogging) Debug.Log($"[RingSessionStore] Loaded {result.Count} rings from {dir}");
        return result;
    }
}
