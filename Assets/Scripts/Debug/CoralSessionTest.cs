using UnityEngine;
using System.Collections.Generic;

public class CoralSessionTest : MonoBehaviour
{
    public CoralVisualizer coralVisualizer;

    void Start()
    {
        if (coralVisualizer == null) return;

        List<PhaseSnapshot> dummySession = new();

        for (int i = 0; i < 8; i++) // 8 phases
        {
            PhaseSnapshot snapshot = new PhaseSnapshot
            {
                pattern = (MusicalPhase)i,
                color = Color.HSVToRGB(i / 8f, 0.6f, 1f),
                timestamp = Time.time + i,
                collectedNotes = new List<PhaseSnapshot.NoteEntry>()
            };

            for (int j = 0; j < Random.Range(20, 40); j++) // 4–10 notes per phase
            {
                int step = Random.Range(0, 64);
                int note = Random.Range(40, 85);
                float velocity = Random.Range(40f, 120f);
                Color trackColor = Color.HSVToRGB(Random.value, 1f, 1f);

                snapshot.collectedNotes.Add(new PhaseSnapshot.NoteEntry(step, note, velocity, trackColor));
            }

            dummySession.Add(snapshot);
        }

        coralVisualizer.snapshots = dummySession;
        coralVisualizer.GenerateCoralFromSnapshots(dummySession);
    }
}