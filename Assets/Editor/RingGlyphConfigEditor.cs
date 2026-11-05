using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Collapses RingGlyphConfig's gameplay-only and track-selection-only fields into two foldout
// drawers so the default inspector shows just the shared look-and-feel settings. Any serialized
// field not listed below falls through to the shared section, so a newly added field can't vanish.
[CustomEditor(typeof(RingGlyphConfig))]
public class RingGlyphConfigEditor : Editor
{
    static readonly HashSet<string> GameplayOnly = new HashSet<string>
    {
        "noteDotRadius", "noteTravelDuration", "launchSfx", "launchSfxVolume", "impactSfx", "impactSfxVolume",
        "noteTetherTransitionDuration",
        "spinOffDuration", "rollOffDuration", "tiltXDegrees", "scaleDownDuration",
        "breakShakeDuration", "breakShakeAmplitude", "breakShakeFrequency",
        "denialShakeDuration", "denialShakeAmplitude", "denialShakeFrequency",
        "recordHoldDuration", "recordFadeOutDuration",
        "nextUpColorCycleDuration",
        "placedBackgroundAlpha", "fullBackgroundAlpha",
    };

    static readonly HashSet<string> TrackSelectionOnly = new HashSet<string>
    {
        "interBinSpacing", "recordLineWidth",
        "highlightScaleMultiplier", "highlightBrightness", "highlightPulseDuration",
        "nonHighlightedTransitionDuration", "nonHighlightedDarkenAmount", "nonHighlightedAlphaMultiplier",
        "directionMatchRatioThreshold", "rotSpeedBase",
    };

    const string GameplayPrefKey = "RingGlyphConfigEditor.GameplayOnly";
    const string TrackSelectionPrefKey = "RingGlyphConfigEditor.TrackSelectionOnly";

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        DrawGroup(null);

        bool gameplay = EditorGUILayout.BeginFoldoutHeaderGroup(
            EditorPrefs.GetBool(GameplayPrefKey, false), "Gameplay Only");
        EditorPrefs.SetBool(GameplayPrefKey, gameplay);
        if (gameplay) DrawGroup(GameplayOnly);
        EditorGUILayout.EndFoldoutHeaderGroup();

        bool trackSel = EditorGUILayout.BeginFoldoutHeaderGroup(
            EditorPrefs.GetBool(TrackSelectionPrefKey, false), "Track Selection Only");
        EditorPrefs.SetBool(TrackSelectionPrefKey, trackSel);
        if (trackSel) DrawGroup(TrackSelectionOnly);
        EditorGUILayout.EndFoldoutHeaderGroup();

        serializedObject.ApplyModifiedProperties();
    }

    // group == null draws every property that belongs to neither drawer.
    void DrawGroup(HashSet<string> group)
    {
        var it = serializedObject.GetIterator();
        for (bool enter = true; it.NextVisible(enter); enter = false)
        {
            if (it.propertyPath == "m_Script") continue;
            bool inGameplay = GameplayOnly.Contains(it.name);
            bool inTrackSel = TrackSelectionOnly.Contains(it.name);
            bool wanted = group == null ? !inGameplay && !inTrackSel : group.Contains(it.name);
            if (wanted) EditorGUILayout.PropertyField(it, true);
        }
    }
}
