using System;
using System.IO;
using UnityEngine;

// Options → Data → Reset All Progress. Erases progress only: rings and local telemetry run
// files. Settings (GameSettings / settings.json: volume, controls, publish choice) are kept —
// Options → Data → Restore Default Settings resets those separately.
//
// publisher_identity.json is kept too, so a later "Don't Publish" can still withdraw this
// player's published records server-side. The only way to drop it is the reset confirm's
// "Erase & Withdraw Published Records", which withdraws first and then calls
// PublisherIdentity.Forget (OptionsMenu). Steam stats/achievements are never touched.
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

    public static void ResetProgress()
    {
        Try("rings", RingSessionStore.DeleteAll);
        Try("telemetry runs", DeleteTelemetryRuns);
        Debug.Log("[SaveData] Reset all progress (settings kept).");
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
