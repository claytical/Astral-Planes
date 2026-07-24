using System;
using System.Collections.Generic;
using UnityEngine;

// Owns bin-allocation/frontier bookkeeping for a track set: which bin a track's next spawn
// should target, hole-filling behind the global frontier, and the "allow advance" token used
// by TrackExpansionController. Extracted from InstrumentTrackController — pure algorithm, no
// VFX/audio/coroutine dependencies, so it needs only a Func into the owning controller's tracks.
public sealed class BinFrontierAllocator
{
    private readonly Func<InstrumentTrack[]> _getTracks;
    private readonly Dictionary<InstrumentTrack, bool> _allowAdvanceNextBurst = new Dictionary<InstrumentTrack, bool>();

    public BinFrontierAllocator(Func<InstrumentTrack[]> getTracks)
    {
        _getTracks = getTracks;
    }

    public void ClearAdvanceTokens() => _allowAdvanceNextBurst.Clear();

    public void AllowAdvanceNextBurst(InstrumentTrack track)
    {
        if (track == null) return;
        // ByVoice chord groups advance together; NotifyBinFilled grants the token
        // to all sibling voices simultaneously when the last one fills the bin.
        var roleProfile = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
        if (roleProfile != null && roleProfile.configSelectionMode == RoleConfigSelectionMode.ByVoice)
            return;
        _allowAdvanceNextBurst[track] = true;
    }

    private bool ConsumeAllowAdvanceNextBurst(InstrumentTrack track)
    {
        if (track == null) return false;

        if (_allowAdvanceNextBurst.TryGetValue(track, out bool allowed) && allowed)
        {
            _allowAdvanceNextBurst[track] = false;
            return true;
        }
        return false;
    }

    public int GetBinForNextSpawn(InstrumentTrack track)
    {
        if (track == null) return 0;
        int trackMaxBinIndex = Mathf.Max(0, track.maxLoopMultiplier - 1);

        // ByVoice chord tracks advance only when the whole chord group fills a bin — use cursor directly.
        var byVoiceCheck = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
        if (byVoiceCheck != null && byVoiceCheck.configSelectionMode == RoleConfigSelectionMode.ByVoice)
        {
            int cursor = track.GetBinCursor();
            if (track.maxLoopMultiplier > 0) cursor %= track.maxLoopMultiplier;
            return Mathf.Clamp(cursor, 0, trackMaxBinIndex);
        }

        // If expansion is already staged, redirect to density injection so SpawnCollectableBurst
        // doesn't reject a frontier proposal and silently drop the burst.
        if (track.IsExpansionPending)
            return Mathf.Clamp(track.GetNextFilledBinForDensity(), 0, trackMaxBinIndex);

        // Refill a bin emptied by note ascension before advancing/expanding.
        int emptied = track.GetFirstEmptyAllocatedBin();
        if (emptied >= 0) return emptied;

        bool allowAdvance = ConsumeAllowAdvanceNextBurst(track); // consume exactly once
        int globalFrontier = ComputeGlobalFrontier();
        if (globalFrontier < 0) return 0;

        int clampedGlobalFrontier = Mathf.Clamp(globalFrontier, 0, trackMaxBinIndex);
        int trackFrontier = FrontierForTrack(track);

        // If this track is behind the global frontier, hole-fill up to the frontier.
        if (trackFrontier < clampedGlobalFrontier)
            return TryFindHoleFillBin(track, clampedGlobalFrontier);

        // Not allowed to advance — use next local bin deterministically.
        if (!allowAdvance)
            return Mathf.Clamp(track.GetNextBinForSpawn(), 0, trackMaxBinIndex);

        // Allowed to advance: wrap cursor into capacity.
        int cursorTarget = track.GetBinCursor();
        if (track.maxLoopMultiplier > 0) cursorTarget %= track.maxLoopMultiplier;
        cursorTarget = Mathf.Clamp(cursorTarget, 0, trackMaxBinIndex);

        // Cursor is pushing beyond global frontier.
        if (cursorTarget > clampedGlobalFrontier)
        {
            if (cursorTarget > trackMaxBinIndex)
                return FindUnfilledOrDensityBin(track, trackMaxBinIndex);
            return cursorTarget;
        }

        // Decide whether to advance based on whether cursor bin is already filled.
        int proposed = track.IsBinFilled(cursorTarget) ? (clampedGlobalFrontier + 1) : cursorTarget;
        if (proposed > trackMaxBinIndex)
            return FindUnfilledOrDensityBin(track, trackMaxBinIndex);
        return proposed;
    }

    // Frontier = furthest bin that is allocated OR filled, cross-checked with cursor estimate.
    private int FrontierForTrack(InstrumentTrack t)
    {
        if (t == null) return -1;
        int highest = Mathf.Max(t.GetHighestFilledBin(), t.GetHighestAllocatedBin());
        int cursorBased = Mathf.Clamp(t.GetBinCursor() - 1, -1, Mathf.Max(0, t.maxLoopMultiplier - 1));
        return Mathf.Max(highest, cursorBased);
    }

    private int ComputeGlobalFrontier()
    {
        int frontier = -1;
        var tracks = _getTracks();
        if (tracks != null)
            for (int i = 0; i < tracks.Length; i++)
                if (tracks[i]) frontier = Mathf.Max(frontier, FrontierForTrack(tracks[i]));
        return frontier;
    }

    private int TryFindHoleFillBin(InstrumentTrack track, int upTo)
    {
        for (int b = 0; b <= upTo; b++)
            if (!track.IsBinAllocated(b)) return b;
        return upTo;
    }

    private int FindUnfilledOrDensityBin(InstrumentTrack track, int trackMaxBinIndex)
    {
        // Complete any allocated-but-unfilled bin before falling back to density injection.
        for (int b = 0; b <= trackMaxBinIndex; b++)
            if (track.IsBinAllocated(b) && !track.IsBinFilled(b)) return b;
        return Mathf.Clamp(track.GetNextFilledBinForDensity(), 0, trackMaxBinIndex);
    }

    public bool IsChordGroupComplete(MusicalRole role, int binIndex)
    {
        var tracks = _getTracks();
        if (tracks == null) return false;
        bool found = false;
        for (int i = 0; i < tracks.Length; i++)
        {
            var t = tracks[i];
            if (t == null || t.assignedRole != role) continue;
            found = true;
            if (!t.IsBinFilled(binIndex)) return false;
        }
        return found;
    }

    public void NotifyBinFilled(InstrumentTrack track, int binIndex)
    {
        if (track == null) return;
        var roleProfile = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
        if (roleProfile == null || roleProfile.configSelectionMode != RoleConfigSelectionMode.ByVoice) return;
        if (!IsChordGroupComplete(track.assignedRole, binIndex)) return;

        // All voices filled this bin together — advance cursors for the whole group so
        // the next GenerateNotes call returns the correct next bin.
        // Do NOT grant _allowAdvanceNextBurst: that token feeds TrackExpansionController,
        // which would passively spawn bin-1 collectables on the next drum downbeat.
        // ByVoice tracks advance only through star pokes (DiscoveryTrackNode ejection).
        var tracks = _getTracks();
        foreach (var t in tracks)
        {
            if (t == null || t.assignedRole != track.assignedRole) continue;
            if (t.GetBinCursor() <= binIndex)
                t.AdvanceBinCursor();
        }
    }
}
