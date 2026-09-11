using System;
using System.IO;
using UnityEngine;

// Player-facing "Publish Flight Plans" preference. Deliberately separate from
// TelemetryConfig.enabled, which is a build/developer toggle, not consent — playing the game,
// enabling Steam Stats, or any other analytics setting must never imply this. No Flight Plan
// leaves the machine unless PublishFlightPlansEnabled is explicitly true.
public static class TelemetryConsent
{
    [Serializable]
    private class ConsentFile
    {
        public bool PublishFlightPlansEnabled;
        public bool HasDecided;
    }

    private static string FilePath =>
        Application.persistentDataPath + "/GreaterMazeTelemetry/publish_consent.json";

    private static ConsentFile _cached;

    public static bool PublishFlightPlansEnabled
    {
        get => LoadFile().PublishFlightPlansEnabled;
        set
        {
            var f = LoadFile();
            f.PublishFlightPlansEnabled = value;
            SaveFile(f);
        }
    }

    // Whether the player has ever made an explicit Publish/Do Not Publish choice — separate from
    // the choice itself, so Main.unity knows whether to offer a settings option to change it.
    public static bool HasDecided
    {
        get => LoadFile().HasDecided;
        set
        {
            var f = LoadFile();
            f.HasDecided = value;
            SaveFile(f);
        }
    }

    private static ConsentFile LoadFile()
    {
        if (_cached != null) return _cached;

        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonUtility.FromJson<ConsentFile>(File.ReadAllText(FilePath));
                _cached = loaded ?? new ConsentFile();
                return _cached;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[TelemetryConsent] Failed to read consent state: {e.Message}");
        }

        _cached = new ConsentFile();
        return _cached;
    }

    private static void SaveFile(ConsentFile file)
    {
        _cached = file;

        try
        {
            string dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonUtility.ToJson(file));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[TelemetryConsent] Failed to persist consent state: {e.Message}");
        }
    }
}
