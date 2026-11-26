using System;
using System.IO;
using UnityEngine;

/// <summary>Which of the two keyboard/mouse control schemes the keyboard player uses.</summary>
public enum KbmScheme
{
    /// <summary>A/D or Left/Right rotate the heading (ship always cruises), W/Up boost, Space place.</summary>
    Keyboard = 0,
    /// <summary>Ship flies toward the cursor, left click boost, right click place.</summary>
    Mouse = 1,
}

/// <summary>
/// Player-facing keyboard/mouse control preference (Settings → Keyboard Controls). Persisted as
/// a small JSON file like <see cref="TelemetryConsent"/>. The names returned by
/// <see cref="SchemeName"/> must match the control schemes in AstralPlanes.inputactions; each scheme
/// pairs both Keyboard and Mouse and acts purely as a binding mask.
/// </summary>
public static class ControlPrefs
{
    public const string KeyboardSchemeName = "Keyboard";
    public const string MouseSchemeName = "Mouse";

    [Serializable]
    private class PrefsFile
    {
        public int KbmScheme;
    }

    private static string FilePath => Application.persistentDataPath + "/control_prefs.json";

    private static PrefsFile _cached;

    /// <summary>Fired after the scheme changes, so joined keyboard players can switch live.</summary>
    public static event Action<KbmScheme> Changed;

    public static KbmScheme Scheme
    {
        get => (KbmScheme)Mathf.Clamp(LoadFile().KbmScheme, 0, 1);
        set
        {
            if (Scheme == value) return;
            var f = LoadFile();
            f.KbmScheme = (int)value;
            SaveFile(f);
            Changed?.Invoke(value);
        }
    }

    public static string SchemeName => NameOf(Scheme);

    public static string NameOf(KbmScheme scheme) =>
        scheme == KbmScheme.Mouse ? MouseSchemeName : KeyboardSchemeName;

    public static bool IsKbmSchemeName(string name) =>
        name == KeyboardSchemeName || name == MouseSchemeName;

    /// <summary>Settings → Reset All Progress.</summary>
    public static void Reset()
    {
        bool wasMouse = Scheme == KbmScheme.Mouse;
        _cached = null;
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ControlPrefs] Failed to delete control prefs: {e.Message}");
        }
        if (wasMouse) Changed?.Invoke(KbmScheme.Keyboard);
    }

    private static PrefsFile LoadFile()
    {
        if (_cached != null) return _cached;

        try
        {
            if (File.Exists(FilePath))
            {
                _cached = JsonUtility.FromJson<PrefsFile>(File.ReadAllText(FilePath)) ?? new PrefsFile();
                return _cached;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ControlPrefs] Failed to read control prefs: {e.Message}");
        }

        _cached = new PrefsFile();
        return _cached;
    }

    private static void SaveFile(PrefsFile file)
    {
        _cached = file;
        try
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(file));
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ControlPrefs] Failed to persist control prefs: {e.Message}");
        }
    }
}
