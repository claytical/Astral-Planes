using System.Text;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(MineNode))]
public class MineNodeDebugEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);

        var node = (MineNode)target;
        if (GUILayout.Button("Log Effective Motion Snapshot"))
            node.LogEffectiveMotionSnapshot();

        if (GUILayout.Button("Validate Archetype Envelope Matrix"))
            LogArchetypeEnvelopeMatrix(node);
    }

    private static void LogArchetypeEnvelopeMatrix(MineNode node)
    {
        var so = new SerializedObject(node);
        var profilesProp = so.FindProperty("locomotionArchetypeProfiles");
        var cfgProp = so.FindProperty("sharedMotionConfig");
        var cfg = cfgProp != null ? cfgProp.objectReferenceValue as MineNodeMotionConfig : null;
        float overlapLimit = cfg != null ? cfg.maxAllowedEnvelopeOverlap : 0.20f;

        var profiles = new MineNodeLocomotionProfile[profilesProp.arraySize];
        for (int i = 0; i < profilesProp.arraySize; i++)
            profiles[i] = profilesProp.GetArrayElementAtIndex(i).objectReferenceValue as MineNodeLocomotionProfile;

        var sb = new StringBuilder();
        sb.AppendLine($"[MineNode Envelope Matrix] overlapLimit={overlapLimit:F2}");
        for (int i = 0; i < profiles.Length; i++)
        {
            if (profiles[i] == null) continue;
            for (int j = i + 1; j < profiles.Length; j++)
            {
                if (profiles[j] == null) continue;
                float speedOverlap = RangeOverlap(profiles[i].baseSpeed, profiles[i].maxSpeed, profiles[j].baseSpeed, profiles[j].maxSpeed);
                float hesitationOverlap = 1f - Mathf.Clamp01(Mathf.Abs(profiles[i].hesitation - profiles[j].hesitation));
                float combined = (speedOverlap + hesitationOverlap) * 0.5f;
                string status = combined <= overlapLimit ? "PASS" : "WARN";
                sb.AppendLine($"{profiles[i].archetype} vs {profiles[j].archetype}: speedOverlap={speedOverlap:F2}, hesitationOverlap={hesitationOverlap:F2}, combined={combined:F2} => {status}");
            }
        }

        Debug.Log(sb.ToString(), node);
    }

    private static float RangeOverlap(float aMin, float aMax, float bMin, float bMax)
    {
        float minA = Mathf.Min(aMin, aMax);
        float maxA = Mathf.Max(aMin, aMax);
        float minB = Mathf.Min(bMin, bMax);
        float maxB = Mathf.Max(bMin, bMax);
        float overlap = Mathf.Max(0f, Mathf.Min(maxA, maxB) - Mathf.Max(minA, minB));
        float span = Mathf.Max(maxA - minA, maxB - minB, 0.0001f);
        return Mathf.Clamp01(overlap / span);
    }
}
