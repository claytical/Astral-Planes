using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using MidiPlayerTK;

/// <summary>
/// Report-only audit for the Options menu's Music / Effects volume (OPTIONS_MENU_DESIGN.md §7.3):
/// lists every AudioSource in prefabs and build scenes whose Output is empty. Those play straight
/// to the listener, so only Master volume reaches them. Code that assigns a group at runtime
/// (MidiVoice role buses, DrumTrack, AudioMixConfig.ResolveEffects users) will still show up here
/// when the serialized field is empty; check the owning component before re-routing.
/// Nothing is modified.
/// </summary>
public static class AudioRoutingReport
{
    [MenuItem("Tools/Astral Planes/Report Unrouted Audio Sources")]
    public static void Run()
    {
        var hits = new List<string>();
        var routedAtRuntime = new List<string>();
        var unused = new List<string>();

        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null) continue;
            foreach (var src in root.GetComponentsInChildren<AudioSource>(true))
                Classify(src, path, hits, routedAtRuntime, unused);
        }

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var setup = EditorSceneManager.GetSceneManagerSetup();
        try
        {
            foreach (var bs in EditorBuildSettings.scenes.Where(s => s.enabled))
            {
                var scene = EditorSceneManager.OpenScene(bs.path, OpenSceneMode.Single);
                foreach (var go in scene.GetRootGameObjects())
                    foreach (var src in go.GetComponentsInChildren<AudioSource>(true))
                        Classify(src, bs.path, hits, routedAtRuntime, unused);
            }
        }
        finally
        {
            if (setup != null && setup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(setup);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[AudioRouting] {hits.Count} AudioSource(s) with no Output group (only Master volume reaches them):");
        foreach (var h in hits) sb.AppendLine("  " + h);
        sb.AppendLine($"Routed by code at runtime ({routedAtRuntime.Count}, no action needed):");
        foreach (var h in routedAtRuntime) sb.AppendLine("  " + h);
        sb.AppendLine($"Never play: MPTK voice templates under a Core-mode player ({unused.Count}, no action needed):");
        foreach (var h in unused) sb.AppendLine("  " + h);
        Debug.Log(sb.ToString());
        EditorUtility.DisplayDialog("Unrouted Audio Sources",
            hits.Count == 0 ? "Every AudioSource has an Output group." : $"{hits.Count} found; the full list is in the Console.",
            "OK");
    }

    // Components that assign outputAudioMixerGroup themselves in Awake/Bind:
    //   AudioManager -> AudioMixConfig.ResolveEffects; MidiStreamPlayer -> MidiVoice.SetMixerGroup
    //   (role bus) or RingPreviewPlayer's per-role clones.
    static void Classify(AudioSource src, string where, List<string> hits, List<string> routed, List<string> unused)
    {
        if (src.outputAudioMixerGroup != null) return;
        string line = $"{where} :: {HierarchyPath(src.transform)}{Owners(src)}";

        // Legacy-mode voice template: cloned per voice only when MPTK_CorePlayer is off.
        var synth = src.GetComponentInParent<MidiSynth>(true);
        if (synth != null && synth.gameObject != src.gameObject && synth.MPTK_CorePlayer) { unused.Add(line); return; }

        if (src.GetComponent<AudioManager>() != null || src.GetComponent<MidiStreamPlayer>() != null) { routed.Add(line); return; }
        hits.Add(line);
    }

    static string HierarchyPath(Transform t)
    {
        var parts = new List<string>();
        for (; t != null; t = t.parent) parts.Insert(0, t.name);
        return string.Join("/", parts);
    }

    // Components on the same GameObject, so it's clear whether code may route it at runtime.
    static string Owners(AudioSource src)
    {
        var names = src.GetComponents<MonoBehaviour>().Where(m => m != null).Select(m => m.GetType().Name).ToArray();
        return names.Length == 0 ? "" : $"  [{string.Join(", ", names)}]";
    }
}
