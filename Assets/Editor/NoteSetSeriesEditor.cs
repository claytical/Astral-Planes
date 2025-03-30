
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(NoteSetSeries))]
public class NoteSetSeriesEditor : Editor
{
    public override void OnInspectorGUI()
    {
        NoteSetSeries series = (NoteSetSeries)target;

        EditorGUILayout.LabelField("Note Set Series", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        series.label = EditorGUILayout.TextField("Label", series.label);
        series.role = (MusicalRole)EditorGUILayout.EnumPopup("Musical Role", series.role);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Curated Note Sets", EditorStyles.boldLabel);
        SerializedProperty noteSetsProp = serializedObject.FindProperty("curatedNoteSets");
        EditorGUILayout.PropertyField(noteSetsProp, true);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Randomization", EditorStyles.boldLabel);
        series.allowRandomRootNote = EditorGUILayout.Toggle("Random Root Note", series.allowRandomRootNote);
        series.allowRandomRhythmStyle = EditorGUILayout.Toggle("Random Rhythm Style", series.allowRandomRhythmStyle);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Weighting", EditorStyles.boldLabel);
        series.selectionWeight = EditorGUILayout.FloatField("Selection Weight", series.selectionWeight);

        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(series);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
        if (GUILayout.Button("Preview Random NoteSet"))
        {
            NoteSet preview = series.GetRandomOrCuratedNoteSet();
            if (preview != null)
            {
                Debug.Log($"[NoteSet Preview] Selected: {preview.name}", preview);
                Selection.activeObject = preview;
            }
            else
            {
                Debug.LogWarning("[NoteSet Preview] No NoteSets available in this series.");
            }
        }
    }
}
