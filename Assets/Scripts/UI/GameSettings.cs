using System;
using System.IO;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;

/// <summary>
/// The single store for every player preference (OPTIONS_MENU_DESIGN.md §5):
/// persistentDataPath/settings.json, versioned, written atomically (.tmp + File.Replace, same as
/// RingSessionStore). <see cref="ControlPrefs"/> and <see cref="TelemetryConsent"/> are thin
/// facades over this so their callers didn't change.
///
/// Value-row edits (volume, groove style…) only mark the file dirty; the Options menu calls
/// <see cref="Flush"/> when a menu closes, and the runtime host flushes on quit. Consent changes
/// save immediately — they must never be lost.
///
/// Window mode is deliberately NOT stored here: Unity persists Screen.fullScreenMode itself
/// (and Alt+Enter changes it outside the menu), so a second copy would only drift.
/// </summary>
public static class GameSettings
{
    public const int CurrentVersion = 1;
    public const string MusicVolumeParam = "MusicVol";
    public const string EffectsVolumeParam = "EffectsVol";

    [Serializable]
    private class SettingsFile
    {
        public int version = CurrentVersion;
        public float masterVolume = 1f;
        public float musicVolume = 1f;
        public float effectsVolume = 1f;
        public bool previewBeat;
        public int grooveStyle = (int)NotePlacementDefaults.Default;
        public int kbmScheme = (int)global::KbmScheme.Keyboard;
        public bool publishEnabled;
        public bool publishDecided;
    }

    // Legacy files imported once, then deleted (§5 migration).
    [Serializable] private class LegacyControlPrefs { public int KbmScheme; }
    [Serializable] private class LegacyConsent { public bool PublishFlightPlansEnabled; public bool HasDecided; }

    private static string Dir => Application.persistentDataPath;
    private static string FilePath => Dir + "/settings.json";
    private static string LegacyControlPath => Dir + "/control_prefs.json";
    private static string LegacyConsentPath => Dir + "/GreaterMazeTelemetry/publish_consent.json";

    private static SettingsFile _file;
    private static bool _dirty;
    private static bool _warnedMissingParams;

    public static event Action<bool> PreviewBeatChanged;

    private static SettingsFile F => _file ?? (_file = Load());

    // ---------------------------------------------------------------- audio

    public static float MasterVolume
    {
        get => Mathf.Clamp01(F.masterVolume);
        set { F.masterVolume = Snap(value); _dirty = true; ApplyAudio(); }
    }

    public static float MusicVolume
    {
        get => Mathf.Clamp01(F.musicVolume);
        set { F.musicVolume = Snap(value); _dirty = true; ApplyAudio(); }
    }

    public static float EffectsVolume
    {
        get => Mathf.Clamp01(F.effectsVolume);
        set { F.effectsVolume = Snap(value); _dirty = true; ApplyAudio(); }
    }

    // ---------------------------------------------------------------- gameplay / controls

    public static bool PreviewBeat
    {
        get => F.previewBeat;
        set
        {
            if (F.previewBeat == value) return;
            F.previewBeat = value;
            _dirty = true;
            PreviewBeatChanged?.Invoke(value);
        }
    }

    /// <summary>Starting Groove Style at ship select; written back whenever a player confirms one.</summary>
    public static NotePlacementMethod GrooveStyle
    {
        get => F.grooveStyle == (int)NotePlacementMethod.Manual ? NotePlacementMethod.Manual : NotePlacementMethod.Auto;
        set { if (F.grooveStyle == (int)value) return; F.grooveStyle = (int)value; _dirty = true; }
    }

    /// <summary>Raw store for <see cref="ControlPrefs.Scheme"/> (which owns the Changed event).</summary>
    internal static KbmScheme KbmScheme
    {
        get => F.kbmScheme == (int)global::KbmScheme.Mouse ? global::KbmScheme.Mouse : global::KbmScheme.Keyboard;
        set { if (F.kbmScheme == (int)value) return; F.kbmScheme = (int)value; _dirty = true; }
    }

    // ---------------------------------------------------------------- publish consent

    internal static bool PublishEnabled => F.publishEnabled;
    internal static bool PublishDecided => F.publishDecided;

    /// <summary>One explicit Publish / Don't Publish choice: both fields, one atomic write.</summary>
    internal static void SetPublishChoice(bool publish)
    {
        F.publishEnabled = publish;
        F.publishDecided = true;
        _dirty = true;
        Flush();
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Writes settings.json if anything changed since the last write.</summary>
    public static void Flush()
    {
        if (!_dirty || _file == null) return;
        if (Save(_file)) _dirty = false;
    }

    /// <summary>Options → Data → Restore Default Settings. Rings, telemetry and the publish
    /// choice are untouched: consent is a deliberate decision, not a preference to "default".</summary>
    public static void RestoreDefaults()
    {
        var old = F;
        _file = new SettingsFile { publishEnabled = old.publishEnabled, publishDecided = old.publishDecided };
        _dirty = true;
        Flush();
        ApplyAudio();
        if (old.previewBeat != _file.previewBeat) PreviewBeatChanged?.Invoke(_file.previewBeat);
        if (old.kbmScheme != _file.kbmScheme) ControlPrefs.RaiseChanged(KbmScheme);
    }

    /// <summary>0..1 → dB for an exposed mixer volume; anything under 0.1% is the mixer's floor.</summary>
    public static float ToDecibels(float v) => v <= 0.001f ? -80f : 20f * Mathf.Log10(v);

    /// <summary>
    /// Master drives AudioListener.volume, so it catches every source no matter how it's routed.
    /// Music and Effects are exposed volumes on ProceduralMixer's Music and Effects groups.
    /// Sources that bypass the mixer only follow Master; see Tools → Astral Planes → Report
    /// Unrouted Audio Sources.
    /// </summary>
    public static void ApplyAudio()
    {
        AudioListener.volume = MasterVolume;

        var mixer = ResolveMixer();
        if (mixer == null) return;
        bool ok = mixer.SetFloat(MusicVolumeParam, ToDecibels(MusicVolume));
        ok &= mixer.SetFloat(EffectsVolumeParam, ToDecibels(EffectsVolume));
        if (!ok && !_warnedMissingParams)
        {
            _warnedMissingParams = true;
            Debug.LogWarning($"[GameSettings] {mixer.name} is missing exposed params '{MusicVolumeParam}'/'{EffectsVolumeParam}' — Music/Effects volume won't apply.");
        }
    }

    private static AudioMixer ResolveMixer()
    {
        var mix = AudioMixConfig.Instance;
        if (mix == null) return null;
        if (mix.effectsGroup != null) return mix.effectsGroup.audioMixer;
        return mix.drumsGroup != null ? mix.drumsGroup.audioMixer : null;
    }

    private static float Snap(float v) => Mathf.Round(Mathf.Clamp01(v) * 20f) / 20f; // 5% grid, no float drift

    // ---------------------------------------------------------------- IO

    private static SettingsFile Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonUtility.FromJson<SettingsFile>(File.ReadAllText(FilePath));
                if (loaded != null) return loaded; // version > Current: read what we know, keep going
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[GameSettings] Failed to read settings, using defaults: {e.Message}");
            return new SettingsFile();
        }

        return MigrateLegacy();
    }

    private static SettingsFile MigrateLegacy()
    {
        var f = new SettingsFile();
        bool any = false;
        try
        {
            if (File.Exists(LegacyControlPath))
            {
                var c = JsonUtility.FromJson<LegacyControlPrefs>(File.ReadAllText(LegacyControlPath));
                if (c != null) f.kbmScheme = Mathf.Clamp(c.KbmScheme, 0, 1);
                any = true;
            }
            if (File.Exists(LegacyConsentPath))
            {
                var c = JsonUtility.FromJson<LegacyConsent>(File.ReadAllText(LegacyConsentPath));
                if (c != null) { f.publishEnabled = c.PublishFlightPlansEnabled; f.publishDecided = c.HasDecided; }
                any = true;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[GameSettings] Failed to read legacy prefs: {e.Message}");
        }

        if (any && Save(f))
        {
            TryDelete(LegacyControlPath);
            TryDelete(LegacyConsentPath);
            Debug.Log("[GameSettings] Migrated control_prefs.json + publish_consent.json into settings.json.");
        }
        return f;
    }

    private static bool Save(SettingsFile f)
    {
        f.version = CurrentVersion;
        string tmp = FilePath + ".tmp";
        try
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
            File.WriteAllText(tmp, JsonUtility.ToJson(f, true));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[GameSettings] Failed to save settings: {e.Message}");
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) { Debug.LogWarning($"[GameSettings] Failed to delete {path}: {e.Message}"); }
    }

    // ---------------------------------------------------------------- runtime host

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Boot()
    {
        _file = null; // domain-reload-off safety
        _dirty = false;
        var go = new GameObject("GameSettingsRuntime") { hideFlags = HideFlags.HideInHierarchy };
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<GameSettingsRuntime>();
    }

    /// <summary>Hidden DDOL host: applies audio once the mixer is live (SetFloat is ignored in
    /// Awake), re-applies per scene, flushes on quit, and runs Options-menu coroutines.</summary>
    internal sealed class GameSettingsRuntime : MonoBehaviour
    {
        internal static GameSettingsRuntime Instance { get; private set; }

        private void Awake() { Instance = this; SceneManager.sceneLoaded += OnSceneLoaded; }
        private void OnDestroy() { SceneManager.sceneLoaded -= OnSceneLoaded; if (Instance == this) Instance = null; }
        private void Start() => ApplyAudio();
        private void OnSceneLoaded(Scene s, LoadSceneMode m) => ApplyAudio();
        private void OnApplicationQuit() => Flush();
        private void OnApplicationPause(bool paused) { if (paused) Flush(); }
    }
}
