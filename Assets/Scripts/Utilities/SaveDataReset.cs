using System;
using System.IO;
using UnityEngine;

// Settings → Reset All Progress. Returns the local install to a first-launch state:
// rings, the Publish choice, and local telemetry run files. Deliberately keeps
// publisher_identity.json (so a later "Don't Publish" can still remove this player's
// published records server-side) and never touches Steam stats/achievements.
// Ring deletions reach Steam Cloud (Auto-Cloud) when the game exits cleanly; if it crashes
// before then, the cloud copies come back on next launch.
public static class SaveDataReset
{
    private static string TelemetryDirectory => Application.persistentDataPath + "/GreaterMazeTelemetry/";

    // Always logged (not gated on VerboseLogging) so Player.log shows where saves really live —
    // e.g. a native Linux build on Steam Deck saves outside the Proton prefix.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void LogSaveLocation()
    {
        Debug.Log($"[SaveData] persistentDataPath = {Application.persistentDataPath} (ring save v{RingSessionStore.CurrentSaveVersion})");
    }

    public static void ResetAll()
    {
        Try("rings", RingSessionStore.DeleteAll);
        Try("publish choice", TelemetryConsent.Reset);
        Try("control prefs", ControlPrefs.Reset);
        Try("telemetry runs", DeleteTelemetryRuns);
        Debug.Log("[SaveData] Reset all progress.");
    }

    private static void DeleteTelemetryRuns()
    {
        if (!Directory.Exists(TelemetryDirectory)) return;

        foreach (var path in Directory.GetFiles(TelemetryDirectory, "run_*.json", SearchOption.TopDirectoryOnly))
            File.Delete(path);

        string sent = Path.Combine(TelemetryDirectory, "Sent");
        if (Directory.Exists(sent)) Directory.Delete(sent, recursive: true);
    }

    private static void Try(string what, Action action)
    {
        try { action(); }
        catch (Exception e) { Debug.LogWarning($"[SaveData] Failed to reset {what}: {e.Message}"); }
    }
}
