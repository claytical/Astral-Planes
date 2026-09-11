using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Sent/unsent bookkeeping for the telemetry retry queue. "Sent" is tracked purely by filesystem
// location — a run file lives under GreaterMazeTelemetry/ until it's uploaded, then gets moved
// into GreaterMazeTelemetry/Sent/. A same-volume File.Move is effectively atomic, so there's no
// window where a separate manifest/sidecar could disagree with the data files it describes.
public static class TelemetryUploadQueue
{
    private static string RootDirectory => Application.persistentDataPath + "/GreaterMazeTelemetry/";
    private static string SentDirectory => RootDirectory + "Sent/";

    public static IEnumerable<string> PendingFiles()
    {
        if (!Directory.Exists(RootDirectory)) yield break;

        // Only run_*.json (written by MazeTelemetryStore) — the directory also holds
        // publisher_identity.json and other non-run files that must never be treated as a run.
        foreach (var path in Directory.GetFiles(RootDirectory, "run_*.json", SearchOption.TopDirectoryOnly))
            yield return path;
    }

    public static void MarkSent(string sourceFilePath)
    {
        if (string.IsNullOrEmpty(sourceFilePath)) return;

        try
        {
            if (!Directory.Exists(SentDirectory)) Directory.CreateDirectory(SentDirectory);
            string dest = Path.Combine(SentDirectory, Path.GetFileName(sourceFilePath));
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(sourceFilePath, dest);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[TelemetryUploadQueue] Failed to mark {sourceFilePath} sent: {e.Message}");
        }
    }
}
