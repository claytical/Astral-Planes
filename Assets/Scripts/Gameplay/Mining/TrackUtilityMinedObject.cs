using System;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Gameplay.Mining
{
    [Serializable]
    public enum TrackModifierType
    {
        Spawner, Clear, Remix,
        RootShift, RhythmStyle,
        ChordProgression
    }
    public enum TrackClearType
    {
        EnergyRestore,
        Remix
    }

    public class TrackUtilityMinedObject : MonoBehaviour
    {
        public TrackModifierType type;
        public ChordProgressionProfile chordProfile;
        private RemixUtility appliedRemix;
        private MinedObject minedObject;
        private MinedObjectSpawnDirective directive;
        public void Initialize(InstrumentTrackController controller, MinedObjectSpawnDirective _directive)
        {
            directive = _directive;
            if (directive == null || directive.remixUtility == null) return;
            minedObject = GetComponent<MinedObject>();
        
            if (minedObject == null) return;
            minedObject.musicalRole = directive.remixUtility.targetRole;

            // 👇 Resolve the actual track tied to the role
            var resolvedTrack = controller.FindTrackByRole(minedObject.musicalRole);
            if (resolvedTrack != null)
            {
                // 👇 This sets assignedTrack correctly for RemixRingHolder to use
                minedObject.AssignTrack(resolvedTrack);
                directive.assignedTrack = resolvedTrack;
            }
            directive = _directive;
        }
    
        private void OnTriggerEnter2D(Collider2D other)
        {
            Vehicle vehicle = other.GetComponent<Vehicle>();
            if(vehicle == null) return;
            CollectionSoundManager.Instance.PlayEffect(SoundEffectPreset.Aether);
        
            if (minedObject.assignedTrack == null)
            {
                Debug.LogWarning("No track assigned to utility item.");
                return;
            }
            vehicle.AddRemixRole(minedObject.assignedTrack, minedObject.musicalRole, directive);
            GetComponent<Explode>()?.Permanent();

        }


        private void AddWalkingNotes(InstrumentTrack track, NoteSet noteSet)
        {
            for (int step = 0; step < track.GetTotalSteps(); step += 2)
            {
                int note = noteSet.GetNextWalkingNote(step);
                int duration = track.CalculateNoteDuration(step, noteSet);
                float velocity = Random.Range(50f, 80f);
                track.GetPersistentLoopNotes().Add((step, note, duration, velocity));
            }
        }

        private void AddPhraseNotes(InstrumentTrack track, NoteSet noteSet)
        {
            for (int step = 0; step < track.GetTotalSteps(); step += 3)
            {
                int note = noteSet.GetPhraseNote(step);
                int duration = track.CalculateNoteDuration(step, noteSet);
                float velocity = Random.Range(70f, 100f);
                track.GetPersistentLoopNotes().Add((step, note, duration, velocity));
            }
        }

        private void AddGrooveNotes(InstrumentTrack track, NoteSet noteSet)
        {
            for (int step = 0; step < track.GetTotalSteps(); step++)
            {
                if (noteSet.IsAccentStep(step))
                {
                    int note = noteSet.GetGrooveNote(out _);
                    int duration = track.CalculateNoteDuration(step, noteSet);
                    float velocity = Random.Range(80f, 110f);
                    track.GetPersistentLoopNotes().Add((step, note, duration, velocity));
                }
            }
        }

        private void AddSustainedNotes(InstrumentTrack track, NoteSet noteSet)
        {
            int totalSteps = track.GetTotalSteps();
            int note = noteSet.GetSustainedNote();
            track.GetPersistentLoopNotes().Add((0, note, totalSteps, 80f));
        }
    
        private void SoloTrackWithBassSupport(InstrumentTrack keepTrack)
        {
            foreach (var track in keepTrack.controller.tracks)
            {
                if (track != keepTrack && track.assignedRole != MusicalRole.Bass)
                    track.ClearLoopedNotes(TrackClearType.Remix);
            }

            var bassTrack = keepTrack.controller.FindTrackByRole(MusicalRole.Bass);
            if (bassTrack != null && bassTrack.GetNoteDensity() < 3)
            {
                RemixTrack(bassTrack);
            }
        }
        private void ApplyChordProgressionToTrack()
        {
            if (chordProfile == null || minedObject.assignedTrack == null) return;

            minedObject.assignedTrack.ClearLoopedNotes(TrackClearType.Remix);
            minedObject.assignedTrack.ApplyChordProgression(chordProfile);

            Debug.Log($"🎵 Overwrote {minedObject.assignedTrack.assignedRole} with progression {chordProfile.name}");
        }

        private void RemixTrack(InstrumentTrack track)
        {
            track.ClearLoopedNotes(TrackClearType.Remix);
            var noteSet = track.GetCurrentNoteSet();
            if (noteSet == null) return;

            var profile = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
            var phase = track.drumTrack.currentPhase;

            noteSet.noteBehavior = profile.defaultBehavior;

            switch (profile.role)
            {
                case MusicalRole.Bass:
                    noteSet.noteBehavior = NoteBehavior.Bass;
                    noteSet.rhythmStyle = (phase == MusicalPhase.Pop) ? RhythmStyle.FourOnTheFloor : RhythmStyle.Sparse;
                    break;
                case MusicalRole.Lead:
                    noteSet.noteBehavior = NoteBehavior.Lead;
                    noteSet.rhythmStyle = RhythmStyle.Syncopated;
                    break;
                case MusicalRole.Harmony:
                    noteSet.noteBehavior = NoteBehavior.Harmony;
                    noteSet.chordPattern = (phase == MusicalPhase.Intensify) ? ChordPattern.Arpeggiated : ChordPattern.RootTriad;
                    break;
                case MusicalRole.Groove:
                    noteSet.noteBehavior = NoteBehavior.Percussion;
                    noteSet.rhythmStyle = RhythmStyle.Dense;
                    break;
            }

            noteSet.Initialize(track.GetTotalSteps());
            AddRandomNotes(track, noteSet, 6); // Main remix pass
            if (track.GetNoteDensity() < 4)
                AddRandomNotes(track, noteSet, 4 - track.GetNoteDensity()); // Fill in to avoid sparseness

            track.controller.UpdateVisualizer();
        }

        private void AddRandomNotes(InstrumentTrack track, NoteSet noteSet, int count)
        {
            var steps = noteSet.GetStepList().OrderBy(_ => Random.value).ToList();
            var notes = noteSet.GetNoteList();

            for (int i = 0; i < Mathf.Min(count, steps.Count); i++)
            {
                int step = steps[i];
                int note = noteSet.GetNextArpeggiatedNote(step);
                int duration = track.CalculateNoteDuration(step, noteSet);
                float velocity = Random.Range(60f, 100f);
                track.GetPersistentLoopNotes().Add((step, note, duration, velocity));
            }
        }
    

        private void ApplySmartStructureShift(InstrumentTrack track)
        {
            if (track.loopMultiplier < track.maxLoopMultiplier && track.GetNoteDensity() >= 4)
            {
                track.ExpandLoop();
            }
            else if (track.loopMultiplier > 1 && track.GetNoteDensity() < 4)
            {
                track.ContractLoop();
            }
            else
            {
                track.ClearLoopedNotes(TrackClearType.Remix);
            }
        }

    }
}