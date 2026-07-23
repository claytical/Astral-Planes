using System;
using UnityEngine;

// Explicit IExpansionHost implementation — the narrow view TrackExpansionController sees of
// this track. Mostly 1-line delegation to methods that live in other partials; must stay on
// InstrumentTrack itself since explicit interface members can't be delegated to a separate class.
public partial class InstrumentTrack
{
    string IExpansionHost.TrackName => name;
    int  IExpansionHost.LoopMultiplier     => loopMultiplier;
    int  IExpansionHost.MaxLoopMultiplier  => maxLoopMultiplier;
    int  IExpansionHost.TotalSteps         => _totalSteps;
    int  IExpansionHost.BinSize            => BinSize();

    void IExpansionHost.SetLoopMultiplier(int v) => loopMultiplier = v;
    void IExpansionHost.SetTotalSteps(int v)     => _totalSteps = v;

    void IExpansionHost.ResetStepCursors()
    {
        _lastLocalStep = -1;
        _lastBarIndex  = -1;
    }

    void IExpansionHost.SetBinAllocated(int bin, bool v) => SetBinAllocated(bin, v);
    void IExpansionHost.SetBinFilled(int bin, bool v)    => SetBinFilled(bin, v);
    void IExpansionHost.EnsureBinList()                  => EnsureBinList();

    int  IExpansionHost.PickRandomExistingBinForDensity() => PickRandomExistingBinForDensity();
    void IExpansionHost.EnqueueNextFrame(Action a)        => EnqueueNextFrame(a);

    void IExpansionHost.ResyncLeaderBinsNow()            => controller?.ResyncLeaderBinsNow();
    void IExpansionHost.EndGravityVoidForPendingExpand()  => controller?.EndGravityVoidForPendingExpand(this);

    void IExpansionHost.RecomputeAllTrackLayouts()
    {
        if (controller?.tracks == null || controller.noteVisualizer == null) return;
        foreach (var t in controller.tracks)
            if (t != null) controller.noteVisualizer.RecomputeTrackLayout(t);
    }

    void IExpansionHost.MarkGhostPaddingOnVisualizer(int oldTotal, int addedSteps) =>
        controller?.noteVisualizer?.MarkGhostPadding(this, oldTotal, addedSteps);

    void IExpansionHost.CanonicalizeTrackMarkersOnVisualizer(int burstId) =>
        controller?.noteVisualizer?.CanonicalizeTrackMarkers(this, currentBurstId);

    void IExpansionHost.UpdateControllerVisualizer() => controller?.UpdateVisualizer();

    int IExpansionHost.GetControllerMaxActiveLoopMultiplier() =>
        controller != null ? controller.GetMaxActiveLoopMultiplier() : 1;

    int IExpansionHost.GetControllerMaxLoopMultiplier() =>
        controller != null ? controller.GetMaxLoopMultiplier() : 1;

    void IExpansionHost.SpawnBurstNow(
        NoteSet noteSet, int maxToSpawn, int burstId,
        Vector3? originWorld, Vector3? repelFromWorld,
        float burstImpulse, float spreadAngleDeg,
        float spawnJitterRadius, BurstPlacementMode placementMode,
        int trapSearchRadiusCells, int trapBufferCells, int forcedTargetBin)
    {
        SpawnCollectableBurst(
            noteSet, maxToSpawn, burstId,
            originWorld, repelFromWorld,
            burstImpulse, spreadAngleDeg,
            spawnJitterRadius, placementMode,
            trapSearchRadiusCells, trapBufferCells,
            forcedTargetBin);
    }
}
