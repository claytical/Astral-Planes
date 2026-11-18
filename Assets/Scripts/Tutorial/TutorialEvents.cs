using System;
using UnityEngine;

/// <summary>
/// Decoupled gameplay signals for the narrated tutorial. Gameplay code only raises these;
/// it has no dependency on tutorial logic. All are no-ops with zero subscribers.
/// </summary>
public static class TutorialEvents
{
    public static event Action CellCarvedByVehicle;
    public static event Action TentacleAppeared;
    public static event Action<Transform> StarArmed;            // the armed star's transform
    public static event Action StarPoked;
    public static event Action DiscoveryBurst;
    public static event Action DiscoveryEscaped;
    public static event Action<Collectable> FirstNoteSpawned;   // first note of a burst to launch
    public static event Action<Collectable> NoteSounded;        // every burst note, as it launches and sounds
    public static event Action<Collectable, Vehicle> NoteCaught;
    public static event Action NotePlaced;
    public static event Action NoteDiscarded;
    public static event Action RingCompleted;
    public static event Action IntroRecordHidden;               // the motif-start ring record has finished fading out
    public static event Action CommitPressedWithoutNote;        // commit/release pressed while carrying nothing
    public static event Action<Vehicle> OutOfEnergyRescued;     // tutorial motif: a vehicle hit 0 energy and was refilled instead of dying

    public static void RaiseCellCarvedByVehicle() => CellCarvedByVehicle?.Invoke();
    public static void RaiseTentacleAppeared()    => TentacleAppeared?.Invoke();
    public static void RaiseStarArmed(Transform star) => StarArmed?.Invoke(star);
    public static void RaiseStarPoked()           => StarPoked?.Invoke();
    public static void RaiseDiscoveryBurst()      => DiscoveryBurst?.Invoke();
    public static void RaiseDiscoveryEscaped()    => DiscoveryEscaped?.Invoke();
    public static void RaiseFirstNoteSpawned(Collectable c) => FirstNoteSpawned?.Invoke(c);
    public static void RaiseNoteSounded(Collectable c) => NoteSounded?.Invoke(c);
    public static void RaiseNoteCaught(Collectable c, Vehicle v) => NoteCaught?.Invoke(c, v);
    public static void RaiseNotePlaced()          => NotePlaced?.Invoke();
    public static void RaiseNoteDiscarded()       => NoteDiscarded?.Invoke();
    public static void RaiseRingCompleted()       => RingCompleted?.Invoke();
    public static void RaiseIntroRecordHidden()   => IntroRecordHidden?.Invoke();
    public static void RaiseCommitPressedWithoutNote() => CommitPressedWithoutNote?.Invoke();
    public static void RaiseOutOfEnergyRescued(Vehicle v) => OutOfEnergyRescued?.Invoke(v);
}
