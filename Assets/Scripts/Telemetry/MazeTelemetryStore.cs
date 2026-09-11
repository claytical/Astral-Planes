using System;
using System.IO;
using UnityEngine;

// Local disk persistence for Greater Maze telemetry. One JSON file per run, never overwritten —
// unlike RingSessionStore's per-(phase,motif)-slot overwrite pattern, this is an append-only
// history intended for later offline aggregation into the public "greater maze" graph.
public static class MazeTelemetryStore
{
    private static string SaveDirectory => Application.persistentDataPath + "/GreaterMazeTelemetry/";

    public static string SaveRun(MazeRunRecord record)
    {
        if (record == null) return null;

        try
        {
            if (!Directory.Exists(SaveDirectory)) Directory.CreateDirectory(SaveDirectory);
            string ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(SaveDirectory, $"run_{record.RunId}_{ts}.json");
            File.WriteAllText(path, JsonUtility.ToJson(record, true));
            if (GameFlowManager.VerboseLogging) Debug.Log($"[MazeTelemetryStore] Saved run → {path}");
            return path;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MazeTelemetryStore] Failed to save run {record.RunId}: {e.Message}");
            return null;
        }
    }
}
