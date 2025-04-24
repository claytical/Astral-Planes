
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "NewNoteSetSeries", menuName = "Astral Planes/NoteSet Series")]
public class NoteSetSeries : ScriptableObject
{
    [Header("Label for Editor Reference")]
    public string label;

    [Header("Assigned Musical Role")]
    public MusicalRole role;

    [Header("Instrument Track Matching")]
    public List<NoteSet> curatedNoteSets;

    [Header("Randomization Settings")]
    public bool allowRandomRootNote = true;
    public bool allowRandomRhythmStyle = true;

    [Header("Optional Weights (future expansion)")]
    public float selectionWeight = 1.0f;

    public NoteSet GetRandomOrCuratedNoteSet()
    {
        if (curatedNoteSets == null || curatedNoteSets.Count == 0)
        {
            Debug.LogWarning("NoteSetSeries has no curated NoteSets. Returning null.");
            return null;
        }

        int index = Random.Range(0, curatedNoteSets.Count);
        Debug.Log($"Choosing note set at index {index}");
        return curatedNoteSets[index];
    }
}
