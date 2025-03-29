using System;
using UnityEngine;
using System.Collections.Generic;
using UnityEngine.UI;

public class NoteVisualizer : MonoBehaviour
{
    public RectTransform playheadLine;
    public DrumTrack drums;
    public InstrumentTrackController tracks;
    private float screenWidth = 1920f;
    public GameObject notePrefab; // Drag in CollectedNoteVisual prefab
    public List<RectTransform> trackRows; // Each track’s row

    private float fullVisualLoopDuration;

    private void Start()
    {
        screenWidth = GetScreenWidth();
    }

    void Update()
    {
        if (playheadLine == null || drums == null || tracks == null)
            return;

        float baseLoopLength = drums.GetLoopLengthInSeconds(); // e.g., 10s
        int longestMultiplier = tracks.GetMaxLoopMultiplier(); // e.g., 4
        float fullVisualLoopDuration = baseLoopLength * longestMultiplier;

        float elapsedTime = (float)(AudioSettings.dspTime - drums.startDspTime);
        float normalized = (elapsedTime % fullVisualLoopDuration) / fullVisualLoopDuration;
        float xPos = normalized * screenWidth;

        Vector2 anchored = playheadLine.anchoredPosition;
        anchored.x = xPos;
        playheadLine.anchoredPosition = anchored;
    }

    private float GetScreenWidth()
    {
        Canvas canvas = GetComponentInParent<Canvas>();
        RectTransform rt = canvas.GetComponent<RectTransform>();
        return rt.rect.width;
    }

    public void DisplayNotes(List<InstrumentTrack> allTracks)
    {
        Clear();

        float stepWidth = screenWidth / drums.totalSteps;

        for (int i = 0; i < allTracks.Count; i++)
        {
            InstrumentTrack track = allTracks[i];
            RectTransform row = trackRows[i];

            foreach (var (step, note, duration, velocity) in track.GetPersistentLoopNotes())
            {
                float x = step * stepWidth;
                float width = duration * stepWidth / 480f; // adjust for MIDI ticks
                float alpha = Mathf.Clamp01(velocity / 127f);

                GameObject noteGO = Instantiate(notePrefab, row);
                RectTransform rt = noteGO.GetComponent<RectTransform>();
                CanvasGroup cg = noteGO.GetComponent<CanvasGroup>();
                Image img = noteGO.GetComponent<Image>();

                rt.anchoredPosition = new Vector2(x, 0f);
                rt.sizeDelta = new Vector2(width, row.sizeDelta.y > 0 ? row.sizeDelta.y : 30f);
                img.color = track.trackColor;
                cg.alpha = alpha;
            }
        }
    }
    private void Clear()
    {
        foreach (var row in trackRows)
        {
            foreach (Transform child in row)
            {
                Destroy(child.gameObject);
            }
        }
    }

}