using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Directions of travel through the motif orbits: Up = outward to the next phase, Down = inward to
/// the previous phase, Left / Right = previous / next motif around the current phase's ring.
/// </summary>
public enum MazeArm { Up = 0, Right = 1, Down = 2, Left = 3 }

public static class MotifMaze
{
    public const int DirectionCount = 4;

    /// <summary>The screen edge a path-choice exit for this direction sits on.</summary>
    public static BoundaryWrap.BoundarySide SideOf(MazeArm arm) => arm switch
    {
        MazeArm.Up    => BoundaryWrap.BoundarySide.Top,
        MazeArm.Right => BoundaryWrap.BoundarySide.Right,
        MazeArm.Down  => BoundaryWrap.BoundarySide.Bottom,
        _             => BoundaryWrap.BoundarySide.Left,
    };
}

/// <summary>
/// Orbit traversal rules over a PhaseLibrary. Phase 0 is the tutorial: an entry gate that leaves
/// the system once completed. Phase p >= 1 is an orbit of any number of motifs (motif index = position
/// on the ring); moving Left/Right goes around the ring (wrapping), Up/Down move to the motif at the same
/// position round the next/previous orbit (see MapMotif).
///
/// Unlock: completing the tutorial opens all of phase 1; a motif in phase p >= 2 opens once the
/// matching motif in phase p - 1 is completed. Up past the outermost orbit loops back to phase 1
/// (once the current motif is completed), and Down from phase 1 loops to the outermost open orbit
/// on that motif index. The tutorial's only move is Up into phase 1, motif 0.
///
/// "Completed" is supplied by the caller (a saved ring exists), keeping this free of persistence.
/// </summary>
public sealed class MotifMazeGraph
{
    private readonly PhaseLibrary _library;
    private readonly Func<int, int, bool> _isCompleted;
    private readonly bool _revealAll;
    private readonly Dictionary<int, int> _remembered = new Dictionary<int, int>();

    public MotifMazeGraph(PhaseLibrary library, Func<int, int, bool> isCompleted, bool revealAll = false)
    {
        _library     = library;
        _isCompleted = isCompleted;
        _revealAll   = revealAll;
    }

    public readonly struct Exit
    {
        public readonly MazeArm Dir;
        public readonly int Phase;
        public readonly int Motif;

        public Exit(MazeArm dir, int phase, int motif)
        {
            Dir   = dir;
            Phase = phase;
            Motif = motif;
        }
    }

    private int PhaseCount => _library != null && _library.phases != null ? _library.phases.Count : 0;

    /// <summary>Motifs authored in this phase (the orbit size); 0 for an unknown phase.</summary>
    public int MotifCount(int phase)
    {
        var phases = _library != null ? _library.phases : null;
        if (phases == null || phase < 0 || phase >= phases.Count) return 0;
        return phases[phase]?.motifs?.Count ?? 0;
    }

    /// <summary>The motif at the same position round another phase's orbit.</summary>
    public int MapMotif(int fromPhase, int motif, int toPhase)
    {
        int from = MotifCount(fromPhase), to = MotifCount(toPhase);
        if (from <= 0 || to <= 0) return 0;
        if (from == to) return motif;
        return Mathf.RoundToInt(motif / (float)from * to) % to;
    }

    /// <summary>Records where the player last sat on this phase's ring, so moving to it from another
    /// phase lands there instead of at the position mapped from the phase being left.</summary>
    public void Remember(int phase, int motif) => _remembered[phase] = motif;

    // The motif to land on in toPhase: the remembered position while it is still open, else the mapped one.
    private int ResolveMotif(int fromPhase, int motif, int toPhase)
    {
        if (_remembered.TryGetValue(toPhase, out int r) && IsVisible(toPhase, r)) return r;
        return MapMotif(fromPhase, motif, toPhase);
    }

    /// <summary>The tutorial counts as done once its ring is saved, or any ring beyond it is (those
    /// can't have been reached otherwise).</summary>
    public bool TutorialComplete
    {
        get
        {
            if (_revealAll || _isCompleted(0, 0)) return true;
            for (int p = 1; p < PhaseCount; p++)
                for (int m = 0; m < MotifCount(p); m++)
                    if (_isCompleted(p, m)) return true;
            return false;
        }
    }

    /// <summary>Every open move from (phase, motif), one per direction, in Up/Right/Down/Left order.</summary>
    public List<Exit> GetExits(int phase, int motif)
    {
        var exits = new List<Exit>(MotifMaze.DirectionCount);
        for (int d = 0; d < MotifMaze.DirectionCount; d++)
        {
            var dir = (MazeArm)d;
            if (TryGetNeighbor(phase, motif, dir, out int nPhase, out int nMotif))
                exits.Add(new Exit(dir, nPhase, nMotif));
        }
        return exits;
    }

    /// <summary>True when the library authors a motif at this position.</summary>
    public bool Exists(int phase, int motif)
    {
        var phases = _library != null ? _library.phases : null;
        if (phases == null || phase < 0 || phase >= phases.Count) return false;
        var motifs = phases[phase]?.motifs;
        return motifs != null && motif >= 0 && motif < motifs.Count && motifs[motif] != null;
    }

    /// <summary>Whether the player can enter this node. The tutorial is only enterable until it is
    /// completed; phase 1 once it is; later phases once the inner motif on the same index is done.</summary>
    public bool IsVisible(int phase, int motif)
    {
        if (!Exists(phase, motif)) return false;
        if (_revealAll) return true;
        if (phase >= 1 && _isCompleted(phase, motif)) return true; // explored motifs always selectable
        if (phase == 0) return !TutorialComplete;
        if (phase == 1) return TutorialComplete;
        return _isCompleted(phase - 1, MapMotif(phase, motif, phase - 1));
    }

    /// <summary>The node reached by moving <paramref name="dir"/> from (phase, motif), if any.</summary>
    public bool TryGetNeighbor(int phase, int motif, MazeArm dir, out int nPhase, out int nMotif)
    {
        nPhase = -1;
        nMotif = -1;

        if (phase == 0)
        {
            if (dir != MazeArm.Up || !Exists(1, 0) || !TutorialComplete) return false;
            nPhase = 1;
            nMotif = 0;
            return true;
        }

        switch (dir)
        {
            case MazeArm.Left:
            case MazeArm.Right:
                return TryGetAround(phase, motif, dir == MazeArm.Right ? 1 : -1, out nPhase, out nMotif);

            case MazeArm.Up:
                return TryGetOutward(phase, motif, out nPhase, out nMotif);

            default:
                return TryGetInward(phase, motif, out nPhase, out nMotif);
        }
    }

    // The next open motif round the ring in this direction, stepping over any that are still locked.
    private bool TryGetAround(int phase, int motif, int step, out int nPhase, out int nMotif)
    {
        nPhase = phase;
        nMotif = motif;
        int count = MotifCount(phase);
        for (int i = 1; i < count; i++)
        {
            int k = ((motif + step * i) % count + count) % count;
            if (!IsVisible(phase, k)) continue;
            nMotif = k;
            return true;
        }
        return false;
    }

    private bool TryGetOutward(int phase, int motif, out int nPhase, out int nMotif)
    {
        nPhase = phase + 1;
        nMotif = ResolveMotif(phase, motif, nPhase);
        if (Exists(nPhase, nMotif))
            return IsVisible(nPhase, nMotif);

        // Outermost orbit for this motif: loop back to phase 1, but only once this node is done
        // (same gate as stepping outward), and never onto itself.
        nPhase = 1;
        nMotif = ResolveMotif(phase, motif, 1);
        return phase != 1 && Exists(1, nMotif) && IsVisible(1, nMotif) && (_revealAll || _isCompleted(phase, motif));
    }

    private bool TryGetInward(int phase, int motif, out int nPhase, out int nMotif)
    {
        if (phase > 1)
        {
            nPhase = phase - 1;
            nMotif = ResolveMotif(phase, motif, nPhase);
            return IsVisible(nPhase, nMotif);
        }

        // Inward of phase 1 loops to the outermost orbit that is open at this orbit position.
        nMotif = motif;
        for (nPhase = PhaseCount - 1; nPhase > 1; nPhase--)
        {
            nMotif = ResolveMotif(1, motif, nPhase);
            if (IsVisible(nPhase, nMotif)) return true;
        }
        return false;
    }
}
