using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Dev-only mix balancing panel for the PhaseLibraryCarousel (toggle with D by default).
/// While open, the carousel preview plays the selected motif's drum loop under its MIDI, routed
/// through the same AudioMixConfig buses and per-role gain trim as the live game, so you can:
///   - step through the motif's drum loops (entryDrumLoops + intensityClipTags),
///   - set the drum level (AudioMixConfig.drumVolume, used live by DrumTrack too),
///   - set each role's MusicalRoleProfile.volumeMultiplier by ear.
/// In the Editor, edits are written back to the assets when the panel closes (or "Save now").
/// The component compiles in every build but does nothing outside the Editor / development builds.
/// Mouse-driven IMGUI, so it never competes with the carousel's arrow / Enter / D-pad polling.
/// </summary>
public class AudioBalanceDebugPanel : MonoBehaviour
{
    [SerializeField] private RingPreviewPlayer previewPlayer;
    [SerializeField] private Key toggleKey = Key.D;
    [Tooltip("Panel width in reference pixels (scaled to a 1080p-tall screen).")]
    [SerializeField] private float panelWidth = 420f;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private bool _open;
    private Vector2 _scroll;
    private readonly HashSet<Object> _dirty = new();

    private void Awake()
    {
        if (previewPlayer == null) previewPlayer = GetComponent<RingPreviewPlayer>();
        if (previewPlayer == null) previewPlayer = FindFirstObjectByType<RingPreviewPlayer>();
    }

    private void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb[toggleKey].wasPressedThisFrame) SetOpen(!_open);
    }

    // Opening forces drums on for balancing; closing leaves them on only if the player's beat toggle
    // (PreviewBeatToggle → RingPreviewPlayer.SetBeat) is on, rather than always switching them off.
    private void SetOpen(bool open)
    {
        _open = open;
        if (previewPlayer != null) previewPlayer.DrumsEnabled = open || previewPlayer.PreferHighIntensity;
        if (!open) Flush();
    }

    private void OnDisable() => Flush();
    private void OnApplicationQuit() => Flush();

    private void MarkDirty(Object o)
    {
        if (o == null) return;
        _dirty.Add(o);
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(o);
#endif
    }

    private void Flush()
    {
        foreach (var o in _dirty) AudioMixConfig.SaveAsset(o);
        _dirty.Clear();
    }

    private void OnGUI()
    {
        if (!_open) return;

        float scale = Screen.height / 1080f;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
        float h = Screen.height / scale - 20f;

        GUILayout.BeginArea(new Rect(10f, 10f, panelWidth, h), GUI.skin.box);
        _scroll = GUILayout.BeginScrollView(_scroll);

        GUILayout.Label($"<b>Audio Balance</b>  ({toggleKey} to close)", Rich());

        if (previewPlayer == null)
        {
            GUILayout.Label("No RingPreviewPlayer found.");
            End();
            return;
        }

        var motif = previewPlayer.CurrentMotif;
        GUILayout.Label(motif != null ? $"Motif: {motif.name}" : "Motif: (none)");
        if (!previewPlayer.IsPlaying)
            GUILayout.Label("Preview stopped — select a played (unlocked) motif.");

        GUILayout.Space(8f);
        DrawDrums();
        GUILayout.Space(8f);
        DrawRoles();
        GUILayout.Space(8f);

        GUILayout.Label(_dirty.Count > 0 ? $"{_dirty.Count} unsaved asset(s) — saved on close." : "All edits saved.");
        if (GUILayout.Button("Save now")) Flush();

        End();
    }

    private void End()
    {
        GUILayout.EndScrollView();
        GUILayout.EndArea();
        GUI.matrix = Matrix4x4.identity;
    }

    private void DrawDrums()
    {
        GUILayout.Label("<b>Drums</b>", Rich());

        var loops = previewPlayer.DrumLoops;
        if (loops.Count == 0)
        {
            GUILayout.Label("This motif has no drum loops.");
        }
        else
        {
            int idx = previewPlayer.DrumLoopIndex;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(32f))) previewPlayer.SetDrumLoop(idx - 1);
            var clip = loops[Mathf.Clamp(idx, 0, loops.Count - 1)];
            GUILayout.Label($"{idx + 1}/{loops.Count}  {(clip != null ? clip.name : "-")}");
            if (GUILayout.Button(">", GUILayout.Width(32f))) previewPlayer.SetDrumLoop(idx + 1);
            GUILayout.EndHorizontal();
        }

        var mix = AudioMixConfig.Instance;
        if (mix == null)
        {
            GUILayout.Label("AudioMixConfig missing (Resources/Config/AudioMixConfig).");
            return;
        }

        var motif = previewPlayer.CurrentMotif;
        if (motif != null)
        {
            float m = Slider($"Motif drum level — {motif.name} (×)", motif.drumVolumeMultiplier, 0f, 2f);
            if (!Mathf.Approximately(m, motif.drumVolumeMultiplier))
            {
                motif.drumVolumeMultiplier = m;
                previewPlayer.RefreshDrumVolume();
                MarkDirty(motif);
            }
        }

        float v = Slider("Global drum volume (all motifs)", mix.drumVolume, 0f, 1f);
        if (!Mathf.Approximately(v, mix.drumVolume))
        {
            mix.drumVolume = v;
            previewPlayer.RefreshDrumVolume();
            MarkDirty(mix);
        }

        float eff = mix.DrumVolumeFor(motif);
        GUILayout.Label(eff >= 0.999f && mix.drumVolume * (motif != null ? motif.drumVolumeMultiplier : 1f) > 1f
            ? $"Effective: {eff:F3} (capped at 1 — raise the Drums bus instead)"
            : $"Effective: {eff:F3}");
    }

    private void DrawRoles()
    {
        GUILayout.Label("<b>Roles (volumeMultiplier)</b>", Rich());

        var roles = previewPlayer.ActiveRoles;
        if (roles.Count == 0) GUILayout.Label("No roles in this recording.");

        foreach (var role in roles)
        {
            var prof = previewPlayer.GetRoleProfile(role);
            if (prof == null)
            {
                GUILayout.Label($"{role}: no profile (using snapshot value)");
                continue;
            }

            float v = Slider($"{role} — {prof.name}", prof.volumeMultiplier, 0f, 2f);
            if (!Mathf.Approximately(v, prof.volumeMultiplier))
            {
                prof.volumeMultiplier = v;
                MarkDirty(prof);
            }
        }
    }

    private static float Slider(string label, float value, float min, float max)
    {
        GUILayout.Label(label);
        GUILayout.BeginHorizontal();
        float v = GUILayout.HorizontalSlider(value, min, max, GUILayout.ExpandWidth(true));
        GUILayout.Label(value.ToString("F3"), GUILayout.Width(52f));
        GUILayout.EndHorizontal();
        // Untouched: return the exact stored value (so drawing never rewrites e.g. 0.883).
        // Dragged: round to 0.01 so the asset doesn't get a long float.
        if (Mathf.Approximately(v, value)) return value;
        return Mathf.Round(v * 100f) / 100f;
    }

    private static GUIStyle _rich;
    private static GUIStyle Rich()
    {
        if (_rich == null) _rich = new GUIStyle(GUI.skin.label) { richText = true };
        return _rich;
    }
#endif
}
