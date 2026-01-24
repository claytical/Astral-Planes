using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public static class ConstellationMemoryStore
{
    private static List<PhaseSnapshot> cachedSnapshots = new();
    private static string SaveDirectory => Application.persistentDataPath + "/CoralSessions/";
    public static SerializablePhaseSession ConvertToSerializable(List<PhaseSnapshot> raw)
    {
        return new SerializablePhaseSession
        {
            sessionTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            snapshots = raw.Select(s => new SerializablePhaseSnapshot
            {
                pattern = s.Pattern.ToString(),
                color = s.Color,
                timestamp = (long)s.Timestamp,
                collectedNotes = s.CollectedNotes.Select(n => new SerializableNoteEntry
                {
                    step = n.Step,
                    note = n.Note,
                    velocity = n.Velocity,
                    trackColor = n.TrackColor
                }).ToList()
            }).ToList()
        };
    }


    public static List<PhaseSnapshot> ConvertFromSerializable(SerializablePhaseSession session)
    {
        return session.snapshots.Select(s => new PhaseSnapshot
        {
            Pattern = Enum.TryParse<MazeArchetype>(s.pattern, out var parsedPhase) ? parsedPhase : MazeArchetype.Establish,
            Color = s.color,
            Timestamp = s.timestamp,
            CollectedNotes = s.collectedNotes.Select(n => new PhaseSnapshot.NoteEntry(
                n.step, n.note, n.velocity, n.trackColor
            )).ToList()
        }).ToList();
    }

    public static void SaveSessionToDisk(List<PhaseSnapshot> snapshots)
    {
        SerializablePhaseSession serial = ConvertToSerializable(snapshots);
        string json = JsonUtility.ToJson(serial, true);

        if (!Directory.Exists(SaveDirectory))
            Directory.CreateDirectory(SaveDirectory);

        string fileName = $"coral_{serial.sessionTimestamp}.json";
        File.WriteAllText(Path.Combine(SaveDirectory, fileName), json);
        Debug.Log($"✅ Coral session saved to {fileName}");
    }

    public static List<List<PhaseSnapshot>> LoadAllSessionsFromDisk()
    {
#if UNITY_EDITOR
        string mockPath = Path.Combine(Application.streamingAssetsPath, "CoralSessions/mock_history.json");
        Debug.Log($"🔍 Checking for mock file at: {mockPath}");
        Debug.Log($"🔍 File.Exists = {File.Exists(mockPath)}");

        if (File.Exists(mockPath))
        {
            Debug.Log("🧪 Using mock coral session history for design testing.");
            string json = File.ReadAllText(mockPath);

            List<SerializablePhaseSession> mockSessions = JsonUtilityWrapper.FromJsonArray<SerializablePhaseSession>(json);
            return mockSessions.Select(ConvertFromSerializable).ToList();
        }
#endif

        var allSessions = new List<List<PhaseSnapshot>>();
        if (!Directory.Exists(SaveDirectory)) return allSessions;

        foreach (var file in Directory.GetFiles(SaveDirectory, "*.json"))
        {
            try
            {
                string json = File.ReadAllText(file);
                SerializablePhaseSession serial = JsonUtility.FromJson<SerializablePhaseSession>(json);
                allSessions.Add(ConvertFromSerializable(serial));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"⚠️ Failed to load {file}: {e.Message}");
            }
        }
        Debug.Log($"📂 Loaded {allSessions.Count} mock coral sessions from JSON");

        return allSessions;
    }

    public static void StoreSnapshot(List<PhaseSnapshot> snapshots)
    {
        cachedSnapshots = snapshots.Select(s => new PhaseSnapshot
        {
            Pattern = s.Pattern,
            Color = s.Color,
            Timestamp = s.Timestamp,
            CollectedNotes = s.CollectedNotes
                .Select(n => new PhaseSnapshot.NoteEntry(n.Step, n.Note, n.Velocity, n.TrackColor))
                .ToList()
        }).ToList(); // Deep copy for safety
    }

    public static List<PhaseSnapshot> RetrieveSnapshot()
    {
        return cachedSnapshots;
    }

    public static void Clear()
    {
        cachedSnapshots.Clear();
    }
}