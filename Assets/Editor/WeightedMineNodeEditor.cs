using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(MineNodeSpawnerSet))]
public class WeightedMineNodeEditor : Editor
{
    SerializedProperty mineNodes;
    SerializedProperty selectionMode;


    private void OnEnable()
    {
        mineNodes = serializedObject.FindProperty("mineNodes");
        selectionMode = serializedObject.FindProperty("selectionMode");

    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        EditorGUILayout.PropertyField(selectionMode, new GUIContent("Selection Mode"));
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("🎯 Weighted Mine Nodes", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        for (int i = 0; i < mineNodes.arraySize; i++)
        {
            SerializedProperty element = mineNodes.GetArrayElementAtIndex(i);
            SerializedProperty prefab = element.FindPropertyRelative("prefab");
            SerializedProperty weight = element.FindPropertyRelative("weight");

            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.PropertyField(prefab, new GUIContent("Prefab"));
            EditorGUILayout.IntSlider(weight, 1, 20, new GUIContent("Weight"));
            if (GUILayout.Button("Remove"))
            {
                mineNodes.DeleteArrayElementAtIndex(i);
            }
            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("➕ Add New Weighted Node"))
        {
            mineNodes.InsertArrayElementAtIndex(mineNodes.arraySize);
        }

        serializedObject.ApplyModifiedProperties();
    }
}