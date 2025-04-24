using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(DrumVolumeMixer))]
public class DrumVolumeMixerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrumVolumeMixer mixer = (DrumVolumeMixer)target;

        EditorGUILayout.LabelField("Current Volume", mixer.currentVolume.ToString("F2"));
        EditorGUILayout.Space();

        if (mixer.influenceToggles == null)
            mixer.influenceToggles = new();

        for (int i = 0; i < mixer.influenceToggles.Count; i++)
        {
            var toggle = mixer.influenceToggles[i];
            if (toggle.influence == null) continue;

            EditorGUILayout.BeginVertical("box");
            toggle.influence.influenceName = EditorGUILayout.TextField("Name", toggle.influence.influenceName);
            toggle.influence.weight = EditorGUILayout.Slider("Weight", toggle.influence.weight, 0f, 1f);
            toggle.enabled = EditorGUILayout.Toggle("Enabled", toggle.enabled);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Remove"))
            {
                mixer.influenceToggles.RemoveAt(i);
                EditorUtility.SetDirty(mixer);
                break;
            }

            if (GUILayout.Button("Ping"))
            {
                EditorGUIUtility.PingObject(toggle.influence);
            }
            EditorGUILayout.EndHorizontal();

            mixer.influenceToggles[i] = toggle; // update back the modified toggle

            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.Space();

        DrumVolumeInfluence newInfluence = EditorGUILayout.ObjectField("Add Influence", null, typeof(DrumVolumeInfluence), false) as DrumVolumeInfluence;
        if (newInfluence != null)
        {
            mixer.influenceToggles.Add(new InfluenceToggle { influence = newInfluence, enabled = true });
            EditorUtility.SetDirty(mixer);
        }

        EditorGUILayout.Space();
        DrawDefaultInspector(); // optional: shows remaining properties like smoothingSpeed, etc.
    }
}
