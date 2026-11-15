using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Single authority for where each musical role and the drum loop are mixed. Live InstrumentTracks,
/// DrumTrack and the carousel's RingPreviewPlayer all route through this table, so every profile of a
/// role lands on the same ProceduralMixer bus. Loaded from Resources/Config/AudioMixConfig.
/// </summary>
[CreateAssetMenu(fileName = "AudioMixConfig", menuName = "Astral Planes/Music/Audio Mix Config")]
public class AudioMixConfig : ScriptableObject
{
    [Serializable]
    public struct RoleBus
    {
        public MusicalRole role;
        public AudioMixerGroup group;
    }

    [Tooltip("Mixer bus per role. A role left out keeps whatever its MidiStreamPlayer is already routed to.")]
    public List<RoleBus> roleBuses = new();

    [Tooltip("Bus the drum loop AudioSources route to (live DrumTrack decks and the carousel preview).")]
    public AudioMixerGroup drumsGroup;

    [Range(0f, 1f)]
    [Tooltip("Base AudioSource volume of the drum loop, live and in the carousel preview.")]
    public float drumVolume = 0.79f;

    private const string ResourcePath = "Config/AudioMixConfig";
    private static AudioMixConfig _instance;
    private static bool _warnedMissing;

    public static AudioMixConfig Instance
    {
        get
        {
            if (_instance != null) return _instance;
            _instance = Resources.Load<AudioMixConfig>(ResourcePath);
            if (_instance == null && !_warnedMissing)
            {
                _warnedMissing = true;
                Debug.LogWarning($"[AudioMixConfig] missing at Resources/{ResourcePath}; mixer routing left unchanged.");
            }
            return _instance;
        }
    }

    /// <summary>Drum AudioSource volume for a motif: global drumVolume × the motif's drumVolumeMultiplier, capped at 1.</summary>
    public float DrumVolumeFor(MotifProfile motif) =>
        Mathf.Clamp01(drumVolume * (motif != null ? Mathf.Max(0f, motif.drumVolumeMultiplier) : 1f));

    /// <summary>Same as DrumVolumeFor, falling back to <paramref name="fallback"/> when no config asset exists.</summary>
    public static float DrumVolumeOrDefault(MotifProfile motif, float fallback)
    {
        var cfg = Instance;
        return cfg != null ? cfg.DrumVolumeFor(motif) : fallback;
    }

    public AudioMixerGroup GetGroup(MusicalRole role)
    {
        if (roleBuses == null) return null;
        foreach (var rb in roleBuses)
            if (rb.role == role) return rb.group;
        return null;
    }

    /// <summary>Editor-only: persist a runtime edit (e.g. from the balance panel) to the asset on disk.</summary>
    public static void SaveAsset(UnityEngine.Object asset)
    {
#if UNITY_EDITOR
        if (asset == null) return;
        UnityEditor.EditorUtility.SetDirty(asset);
        UnityEditor.AssetDatabase.SaveAssetIfDirty(asset);
#endif
    }
}
