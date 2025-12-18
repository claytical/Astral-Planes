using System;
using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Gameplay.Mining;
using MidiPlayerTK;
using Random = UnityEngine.Random;
public static class ShipTrackAssigner
{
    public static void AssignShipsToTracks(List<ShipMusicalProfile> selectedShips, List<InstrumentTrack> tracks)
    {
        List<InstrumentTrack> unassignedTracks = new List<InstrumentTrack>(tracks);

        // Step 1: Assign presets from ships
        foreach (var ship in selectedShips)
        {
            foreach (var track in unassignedTracks.ToList())
            {
                var roleProfile = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
                if (roleProfile == null || roleProfile.allowedMidiPresets == null) continue;

                if (ship.allowedMidiPresets.Any(p => roleProfile.allowedMidiPresets.Contains(p)))
                {
                    var validPresets = ship.allowedMidiPresets
                        .Where(p => roleProfile.allowedMidiPresets.Contains(p))
                        .ToList();

                    if (validPresets.Count == 0) continue;

                    int preset = validPresets[Random.Range(0, validPresets.Count)];
                    track.preset = preset;
                    var noteSet = new NoteSet { assignedInstrumentTrack = track, noteBehavior = roleProfile.defaultBehavior}; 
                    noteSet.Initialize(track, track.GetTotalSteps());
                    unassignedTracks.Remove(track);
                    break;
                }
            }
        }

        // Step 2: Assign remaining tracks randomly
        foreach (var track in unassignedTracks)
        {
            var fallbackProfile = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
            if (fallbackProfile == null || fallbackProfile.allowedMidiPresets.Count == 0) continue;

            int randomPreset = fallbackProfile.allowedMidiPresets[Random.Range(0, fallbackProfile.allowedMidiPresets.Count)];
            track.preset = randomPreset;

            var noteSet = new NoteSet { assignedInstrumentTrack = track, noteBehavior = fallbackProfile.defaultBehavior };
                        noteSet.Initialize(track, track.GetTotalSteps());
        }
    }
}

public class InstrumentTrackController : MonoBehaviour
{
    public InstrumentTrack[] tracks;
    public NoteVisualizer noteVisualizer;
    private readonly Dictionary<InstrumentTrack, int> _loopHash = new();
    [SerializeField] private MusicalRole cohortTriggerRole = MusicalRole.Lead;
    [SerializeField] private float cohortWindowFraction = 0.5f; // e.g., lower half of the leader loop (0..16 when leader is 32)
    private bool _chordEventsSubscribed;
    public float lastCollectionTime { get; private set; } = -1f;
    private readonly HashSet<(InstrumentTrack track, int bin)> _binExtensionSignaled = new();
    public void NotifyBinPossiblyExtended(InstrumentTrack leaderTrack, int binIndex)
    {
        if (!leaderTrack || binIndex < 0) return;
        // Only act the FIRST time we see this (track, bin) as an extension
        if (_binExtensionSignaled.Add((leaderTrack, binIndex)))
            AdvanceOtherTrackCursors(leaderTrack, 1);
    }
    public void NotifyCollected() {
        lastCollectionTime = Time.time;
    }
    
    public void ResetControllerBinGuards()
    {
        _binExtensionSignaled?.Clear();
    }

    void Start()
    {
        if (!GameFlowManager.Instance.ReadyToPlay()) return;
        noteVisualizer?.Initialize(); // ← ensures playhead + mapping are active
        ResetAllCursorsAndGuards(clearLoops:false);
        UpdateVisualizer();
        // Subscribe to ascension-complete events
        foreach (var t in tracks)
            if (t != null)
            {
                t.OnAscensionCohortCompleted -= HandleAscensionCohortCompleted; // avoid dupes
                t.OnAscensionCohortCompleted += HandleAscensionCohortCompleted;
                Debug.Log($"[CHORD][SUB] Controller subscribed to CohortCompleted for track={t.name} role={t.assignedRole} id={t.GetInstanceID()}");
            }
        // Subscribe to the drum’s loop boundary so we (re)arm each loop
        var drum = GameFlowManager.Instance.activeDrumTrack;
        TrySubscribeChordEvents(); 
        if (drum != null)
            drum.OnLoopBoundary += ArmCohortsOnLoopBoundary;
        ArmCohortsOnLoopBoundary();
    }
    void Update()
    {
        // Self-heal: if something reassigns tracks later, we’ll latch subscriptions once.
        if (!_chordEventsSubscribed)
            TrySubscribeChordEvents();
    }
    void OnEnable()
    {
        _chordEventsSubscribed = false;
        TrySubscribeChordEvents();   // first attempt
    }
    void OnDisable()
    {
        UnsubscribeChordEvents();
    }
    private void TrySubscribeChordEvents()
    {
        // Prefer our own tracks array; if it isn't set, try to pull from the active controller
        var src = tracks;
        if (src == null || src.Length == 0)
        {
            var ctrl = GameFlowManager.Instance ? GameFlowManager.Instance.controller : null;
            if (ctrl != null && ctrl.tracks != null && ctrl.tracks.Length > 0)
                src = ctrl.tracks;
        }
        if (src == null || src.Length == 0) return; // not ready yet

        int count = 0;
        foreach (var t in src)
        {
            if (!t) continue;
            // De-dupe to avoid multiple adds if TrySubscribe runs more than once
            t.OnAscensionCohortCompleted -= HandleAscensionCohortCompleted;
            t.OnAscensionCohortCompleted += HandleAscensionCohortCompleted;
            t.OnCollectableBurstCleared -= HandleCollectableBurstCleared;
            t.OnCollectableBurstCleared += HandleCollectableBurstCleared;

            count++;
        }
        if (count > 0)
        {
            tracks = src; // keep the exact instances we subscribed to
            _chordEventsSubscribed = true;
            Debug.Log($"[CHORD][SUB] Subscribed to CohortCompleted on {count} tracks");
        }
    }
    private void HandleCollectableBurstCleared(InstrumentTrack track, int burstId)
    {
        // We only want to advance when ALL collectables are gone (across tracks).
        if (AnyCollectablesInFlight()) return;

        // At this moment, the last collectable note has been collected across the whole system.
        // Notify the PhaseStar (or the PhaseTransitionManager, depending on your architecture).
        var gfm = GameFlowManager.Instance;
        if (gfm != null && gfm.activeDrumTrack._star != null) // or however you access the active PhaseStar
            gfm.activeDrumTrack._star.NotifyCollectableBurstCleared();
    }

    private void UnsubscribeChordEvents()
    {
        if (tracks == null) return;
        foreach (var t in tracks)
        {
            if (!t) continue;
            t.OnAscensionCohortCompleted -= HandleAscensionCohortCompleted;
        }
        _chordEventsSubscribed = false;
    }
    private void OnDestroy()
    {
        // tidy subscriptions
        var drum = GameFlowManager.Instance ? GameFlowManager.Instance.activeDrumTrack : null;
        if (drum != null) drum.OnLoopBoundary -= ArmCohortsOnLoopBoundary;
        foreach (var t in tracks)
            if (t != null)
                t.OnAscensionCohortCompleted -= HandleAscensionCohortCompleted;
    }
// In InstrumentTrackController.cs
    public float GetEffectiveLoopLengthInSeconds()
    {
        var gfm  = GameFlowManager.Instance;
        var drum = gfm != null ? gfm.activeDrumTrack : null;
        if (drum == null)
            return 0f;

        // IMPORTANT: use the *clip* length, not DrumTrack.GetLoopLengthInSeconds()
        float clipLen = drum.GetClipLengthInSeconds();
        int   totalSteps = drum.totalSteps;
        if (clipLen <= 0f || totalSteps <= 0)
            return clipLen;

        // LeaderSteps already looks at track loopMultipliers
        int leaderSteps = drum.GetLeaderSteps();
        if (leaderSteps <= 0)
            return clipLen;

        float stepDuration = clipLen / totalSteps;
        return stepDuration * leaderSteps;
    }

    private void ArmCohortsOnLoopBoundary()
    {
        var drum = GameFlowManager.Instance.activeDrumTrack;
        int leaderSteps = (drum != null) ? drum.GetLeaderSteps() : 0;
        if (leaderSteps <= 0)
        {
            // Fallback: use max of actual tracks if drum not ready yet
            leaderSteps = tracks.Where(t => t != null).Select(t => t.GetTotalSteps()).DefaultIfEmpty(32).Max();
        }

        int start = 0;
        int endLeader = Mathf.Max(1, Mathf.RoundToInt(leaderSteps * Mathf.Clamp01(cohortWindowFraction)));

        foreach (var t in tracks)
        {
            if (t == null) continue;

            int trackSteps = Mathf.Max(1, t.GetTotalSteps()); // <- NO extra * loopMultiplier
            // Map [0..endLeader) from leader-space into this track’s local modulus
            int endTrack = Mathf.Clamp(endLeader, 1, trackSteps);

            t.ArmAscensionCohort(0, endTrack);

            Debug.Log($"[CHORD][ARM] {t.name} role={t.assignedRole} window=[0,{endTrack}) " +
                      $"trackSteps={trackSteps} leaderSteps={leaderSteps} " +
                      $"armed={t.ascensionCohort.armed} remaining={(t.ascensionCohort.stepsRemaining!=null?t.ascensionCohort.stepsRemaining.Count:0)}");
        }
    }
public int GetBinForNextSpawn(InstrumentTrack track)
{
    if (track == null)
        return 0;

    // 1) Compute globalMaxFilledBin across all configured tracks
    int globalMaxFilledBin = -1;
    if (tracks != null)
    {
        for (int i = 0; i < tracks.Length; i++)
        {
            var t = tracks[i];
            if (!t) continue;

            int h = t.GetHighestFilledBin();
            if (h > globalMaxFilledBin)
                globalMaxFilledBin = h;
        }
    }

    // No one has any notes yet → first burst always goes to bin 0.
    if (globalMaxFilledBin < 0)
        return 0;

    // 2) Track-local filled extent
    int trackHighest = track.GetHighestFilledBin();

    if (trackHighest < globalMaxFilledBin)
    {
        // This track is "behind" the frontier → catch up by filling holes
        // in bins [0 .. globalMaxFilledBin] where it has no notes yet.
        for (int b = 0; b <= globalMaxFilledBin; b++)
        {
            if (!track.IsBinFilled(b))
                return b;
        }

        // Defensive fallback: if somehow all bins up to globalMaxFilledBin are filled
        // on this track too, treat it as caught up and advance the frontier.
        return globalMaxFilledBin + 1;
    }
    else
    {
        // This track has caught up with the current frontier (or defines it).
        // Allow it to push the frontier forward into the next bin.
        return globalMaxFilledBin + 1;
    }
}

private void ResetAllCursorsAndGuards(bool clearLoops=false)
    {
        ResetControllerBinGuards();
        if (tracks == null) return;
        foreach (var t in tracks)
            if (t) t.ResetBinsForPhase();
    }
    public bool AnyCollectablesInFlight() { 
        if (tracks == null || tracks.Length == 0) return false; 
        foreach (var t in tracks)
        {
            if (!t || t.spawnedCollectables == null) continue; 
            // Fast path: if the list is non-empty, assume "in flight".
            if (t.spawnedCollectables.Count > 0) return true;
        } 
        Debug.Log($"[COLLECTABLES] No more collectables in flight");
        return false;
    }
public void AdvanceOtherTrackCursors(InstrumentTrack leaderTrack, int by = 1)
{
    if (tracks == null) return;
    for (int i = 0; i < tracks.Length; i++)
    {
        var t = tracks[i];
        if (!t || t == leaderTrack) continue;
        t.AdvanceBinCursor(by); // silent bin reserved; visuals omitted by design
    }
}
    private void HandleAscensionCohortCompleted(InstrumentTrack track, int start, int end)
    {
        Debug.Log($"[CHORD][CTRLR] CohortCompleted received from track={track.name} role={track.assignedRole} window=[{start},{end})");

        // If you intend to restrict to a specific role, keep this. Otherwise remove it.
        // if (track.assignedRole != cohortTriggerRole) { Debug.Log("[CHORD][CTRLR] Ignored: not trigger role"); return; }

        var h = GameFlowManager.Instance ? GameFlowManager.Instance.harmony : null;
        if (h == null) { Debug.LogWarning("[CHORD][CTRLR] HarmonyDirector is NULL"); return; }

        // This is your “tick”: the armed cohort finished ascending on 'track'
        // 1) Optionally: small flourish / feedback hook could go here
        
        // 2) Ask HarmonyDirector to advance one chord and retune everyone
        GameFlowManager.Instance?.harmony?.AdvanceChordAndRetuneAll(1);
    }
    public void ConfigureTracksFromShips(List<ShipMusicalProfile> selectedShips)
    {
        ShipTrackAssigner.AssignShipsToTracks(selectedShips, tracks.ToList());
        UpdateVisualizer();
    } 
    public InstrumentTrack GetAmbientContextTrack() {
        if (tracks == null || tracks.Length == 0) return null; 
        // Prefer Harmony → Groove → Bass → Lead (falls back to first that has a NoteSet)
        MusicalRole[] pref = { MusicalRole.Harmony, MusicalRole.Groove, MusicalRole.Bass, MusicalRole.Lead }; 
        foreach (var role in pref) {
            var t = tracks.FirstOrDefault(x => x != null && x.assignedRole == role && x.HasNoteSet()); if (t != null) return t;
        } 
        return tracks.FirstOrDefault(x => x != null && x.HasNoteSet()) ?? tracks[0];
    }
    public NoteSet GetGlobalContextNoteSet(){ var t = GetAmbientContextTrack(); 
        return t != null ? t.GetActiveNoteSet() : null;
        }
    public InstrumentTrack FindTrackByRole(MusicalRole role)
    {
        Debug.Log("Configured roles: " + 
                  string.Join(", ", tracks.Select(t => t.assignedRole)));
        return tracks.FirstOrDefault(t => t.assignedRole == role);
    }
    public void ApplySeedVisibility(List<InstrumentTrack> seeds)
    {
        var seedSet = new HashSet<InstrumentTrack>(seeds ?? new List<InstrumentTrack>());
        foreach (var t in tracks)
        {
            bool on = seedSet.Contains(t);
        }
        // Optionally fade unmuted ones back in over ~0.5s
    }
    private IEnumerator FadeOutMidi(MidiStreamPlayer player, float duration)
    {
        float startVolume = player.MPTK_Volume;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            player.MPTK_Volume = Mathf.Lerp(startVolume, 0f, elapsed / duration);
            yield return null;
        }

        player.MPTK_Volume = 0f;
    }
    public void ApplyPhaseSeedOutcome(MusicalPhase nextPhase, List<InstrumentTrack> seeds)
    {
        var seedSet = new HashSet<InstrumentTrack>(seeds ?? new List<InstrumentTrack>());
        foreach (var t in tracks)
        {
            if (t == null) continue;
            if (seedSet.Contains(t))
                RemixSeedForPhase(t, nextPhase);     // keep + lightly remix for the new phase
            else
                ClearTrackForNewPhase(t);            // silence + clear objects so the new star can rebuild
        }
        ResetAllCursorsAndGuards(false);
    }
    public InstrumentTrack GetTrackByRole(MusicalRole role)
    {
        foreach (var t in tracks)
            if (t.assignedRole == role) return t;
        return null;
    }

    public void UpdateVisualizer()
    {
        if (noteVisualizer == null) return;

        foreach (var track in tracks)
        {
            int h = ComputeLoopHash(track);
            if (_loopHash.TryGetValue(track, out var prev) && prev == h)
                continue; // no loop change → no work this frame
            _loopHash[track] = h;

            foreach (var (step, _, _, _) in track.GetPersistentLoopNotes())
                noteVisualizer.PlacePersistentNoteMarker(track, step); // lit=true default
        }
    }
    private static int ComputeLoopHash(InstrumentTrack t)
    {
        // order-independent hash of loop steps (cheap + stable)
        unchecked
        {
            int h = 17;
            foreach (var (step, _, _, _) in t.GetPersistentLoopNotes().OrderBy(n => n.Item1))
                h = h * 31 + step;
            return h;
        }
    }
    public int GetMaxActiveLoopMultiplier()
    {
        if (tracks == null || tracks.Length == 0) return 1;

        int maxMul = 1;
        foreach (var t in tracks)
        {
            if (t == null) continue;
            var loopNotes = t.GetPersistentLoopNotes();
            if (loopNotes != null && loopNotes.Count > 0)
                maxMul = Mathf.Max(maxMul, Mathf.Max(1, t.loopMultiplier));
        }
        return maxMul;
    }
    public int GetMaxLoopMultiplier()
    {
        return tracks.Max(track => track.loopMultiplier);
    }
    public void BeginGameOverFade()
    {
        foreach (var track in tracks)
        {
            if (track == null) continue;

            var loopNotes = track.GetPersistentLoopNotes();
            for (int i = 0; i < loopNotes.Count; i++)
            {
                var (step, note, _, velocity) = loopNotes[i];
                int longDuration = 1920; // ≈4 beats (1 bar) at 480 ticks per beat
                loopNotes[i] = (step, note, longDuration, velocity);
            }
            // Start fading out this track's MIDI stream
            if (track.midiStreamPlayer != null)
            {
                track.StartCoroutine(FadeOutMidi(track.midiStreamPlayer, 2f));
            }
        }

        
    }
    private void ClearTrackForNewPhase(InstrumentTrack t)
{
    
    // 2) Despawn/clear any lingering collectables/mined objects on this track
    //    (prevents "already perfect" and stale rot artifacts)
    if (t.spawnedCollectables != null)
    {
        for (int i = t.spawnedCollectables.Count - 1; i >= 0; i--)
        {
            var go = t.spawnedCollectables[i];
            if (go) Destroy(go);
            t.spawnedCollectables.RemoveAt(i);
        }
    }
    
    // 4) Optional: visual nudge (e.g., dim ribbon color for this track)
    // NoteVisualizer can read t.IsMuted to dim rows, if you want.
}
    public void RemixAllTracksForBridge(MusicalPhase phase, PhaseBridgeSignature sig)
{
    if (tracks == null) return;
    foreach (var t in tracks)
        RemixTrackForBridge(t, phase, sig);
}
    private void RemixTrackForBridge(InstrumentTrack track, MusicalPhase phase, PhaseBridgeSignature sig)
{
    if (track == null) return;

    // 1) Clear the loop so the bridge is audibly different
//    track.ClearLoopedNotes(TrackClearType.Remix);

    // 2) Get / ensure a NoteSet
    var ns = track.GetActiveNoteSet();
    if (ns == null) return; // nothing to remix

    // 3) Phase/role defaults, then bridge overrides
    var (defBehavior, defRhythm) = GetDefaultStyleForPhaseAndRole(phase, track.assignedRole);

    ns.ChangeNoteBehavior(track, defBehavior);     // you already use this elsewhere
    ns.rhythmStyle = defRhythm;                    // rhythm impacts durations downstream

    // 4) Inline seeding: pick a few allowed steps and add arpeggiated notes
    var steps = ns.GetStepList();               // <-- you use this pattern already
    if (steps == null || steps.Count == 0) return;

    var fresh = new List<(int step,int note,int duration,float velocity)>();
    int seeds = 8; // a touch denser for lift-off; tweak to taste
    for (int i = 0; i < seeds; i++) { 
        int step = steps[UnityEngine.Random.Range(0, steps.Count)]; 
        int note = ns.GetNextArpeggiatedNote(step); 
        int dur  = track.CalculateNoteDuration(step, ns); 
        float vel = Mathf.Lerp(70f, 110f, UnityEngine.Random.value); 
        fresh.Add((step, note, dur, vel));
    } 
    if (fresh.Count < 6) { 
        int topUp = 6 - fresh.Count; 
        for (int i = 0; i < topUp; i++) {
            int step = steps[UnityEngine.Random.Range(0, steps.Count)]; 
            int note = ns.GetNextArpeggiatedNote(step); 
            int dur  = track.CalculateNoteDuration(step, ns); 
            float vel = Mathf.Lerp(80f, 115f, UnityEngine.Random.value); 
            fresh.Add((step, note, dur, vel));
        }
    }
    // Atomic swap: no blast visuals, no collapse-to-x1
    track.SoftReplaceLoop(fresh);
}
    private void RemixSeedForPhase(InstrumentTrack t, MusicalPhase phase)
{

    // Nudge its pattern/behavior so the new phase has a recognizable seed
    var ns = t.GetActiveNoteSet();
    if (ns != null)
    {
        var (behavior, rhythm) = GetDefaultStyleForPhaseAndRole(phase, t.assignedRole);
        ns.ChangeNoteBehavior(t, behavior);
        ns.rhythmStyle = rhythm;

        // Optional: quick spice if you’re re-integrating “remix boost”
        // ns.RandomizeArpOrder();
        // ns.ShiftOctave(phase == MusicalPhase.Intensify ? +1 : 0);
    }

    // Also clear old collectables to avoid stale rot on seeds
    if (t.spawnedCollectables != null)
    {
        for (int i = t.spawnedCollectables.Count - 1; i >= 0; i--)
        {
            var go = t.spawnedCollectables[i];
            if (go) Destroy(go);
            t.spawnedCollectables.RemoveAt(i);
        }
    }
}
    private (NoteBehavior behavior, RhythmStyle rhythm) GetDefaultStyleForPhaseAndRole(MusicalPhase phase, MusicalRole role)
{
    switch (phase)
    {
        case MusicalPhase.Intensify:
            return role == MusicalRole.Groove
                ? (NoteBehavior.Percussion, RhythmStyle.Dense)
                : (NoteBehavior.Lead, RhythmStyle.Syncopated);

        case MusicalPhase.Release:
            return (NoteBehavior.Drone, RhythmStyle.Sparse);

        case MusicalPhase.Evolve:
            return (NoteBehavior.Lead, RhythmStyle.Steady);

        case MusicalPhase.Wildcard:
            return (NoteBehavior.Glitch, RhythmStyle.Triplet);

        case MusicalPhase.Pop:
            return (NoteBehavior.Harmony, RhythmStyle.Steady);

        default: // Establish
            return (NoteBehavior.Lead, RhythmStyle.Steady);
    }
}
}
