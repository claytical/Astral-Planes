using System.Collections.Generic;
using UnityEngine;

// Tentacles as living vines: each tentacle grows along a world-space route (TentacleVineRoute)
// that goes around cosmic dust, breaks through dust only when no open route exists, and keeps
// its path to the target when the star is bumped — only the root end stretches/bends to follow.
public partial class PhaseStarDustAffect
{
    private readonly List<Vector2> _vineNodesScratch = new List<Vector2>(32);

    private float CellWorld(DrumTrack drum) => drum != null ? Mathf.Max(0.01f, drum.GetCellWorldSize()) : 1f;

    /// <summary>
    /// Plans a dust-avoiding route star → target. First pass protects the attuned role's dust
    /// (breaking it would throw away the star's own zap energy); falls back to allowing it.
    /// Returns the number of dust cells the route crosses.
    /// </summary>
    private int PlanVineRoute(Tentacle tentacle, Vector2 starPos, DrumTrack drum, List<Vector2> outNodes)
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var gen = _gfm?.dustGenerator;
        var target = tentacle.targetCell;

        bool HardCommon(Vector2Int c) =>
            (gen != null && gen.IsBlockAt(c)) || IsReservedByAnotherTentacle(c, tentacle);

        bool HardProtectRole(Vector2Int c)
        {
            if (HardCommon(c)) return true;
            return _attunedRole != MusicalRole.None && gen != null &&
                   gen.TryGetDustAt(c, out var d) && d != null && d.Role == _attunedRole;
        }

        if (_vinePlanner.TryPlan(drum, starPos, target, HardProtectRole, outNodes, out int crossings))
            return crossings;
        _vinePlanner.TryPlan(drum, starPos, target, HardCommon, outNodes, out crossings);
        return crossings;
    }

    private void BeginVineRoute(Tentacle tentacle, Vector2 starPos, DrumTrack drum)
    {
        PlanVineRoute(tentacle, starPos, drum, _vineNodesScratch);
        float cellW = CellWorld(drum);
        _vinePlanner.AddMeander(drum, _vineNodesScratch, vineMeander, vineMeanderMaxCells * cellW,
            vineMeanderSpacingCells * cellW,
            (tentacle.lineIndexInRole + (tentacle.flowOffset > 0.5f ? 1 : 0)) % 2 == 0 ? 1f : -1f);
        tentacle.route.SetNodes(_vineNodesScratch);
        tentacle.route.FrontDistFromTarget = tentacle.route.TotalLength;

        // Tuning-compatible speed: a straight shot takes 1/tentacleGrowSpeed seconds like before;
        // detours around dust take proportionally longer.
        float straight = Mathf.Max(CellWorld(drum), Vector2.Distance(starPos, tentacle.targetWorldPos));
        tentacle.growUnitsPerSec = Mathf.Max(0.01f, tentacleGrowSpeed * straight);

        tentacle.boring = false;
        tentacle.boreTimer = 0f;
        tentacle.lastRootPos = starPos;
        tentacle.rootCrumbs = 0;
        tentacle.rootRenderInit = false;
    }

    /// <summary>
    /// Keeps the vine's root on the star without disturbing the planned route: once a vine has
    /// locked onto its path it holds that shape until it zaps. Only the stretch next to the star
    /// moves — it stretches straight while that line is dust-free, and pins a breadcrumb corner
    /// when the star moves behind dust. Only those breadcrumbs (never planned corners) are removed
    /// again when the star comes back or regains line of sight.
    /// </summary>
    private void UpdateVineRoot(Tentacle tentacle, Vector2 starPos, DrumTrack drum)
    {
        var route = tentacle.route;
        if (route.Count < 2 || drum == null) return;

        float cell  = CellWorld(drum);
        float slack = cell * vineRootSlackCells;
        Vector2Int starCell = drum.WorldToGridPosition(starPos);

        // Pushed back along its own breadcrumbs → drop the ones the star is now past.
        for (int k = Mathf.Min(tentacle.rootCrumbs, route.Count - 2); k >= 2; k--)
        {
            if (Vector2.Distance(starPos, route.nodes[k]) < slack)
            {
                route.RemoveAfterRoot(k - 1);
                tentacle.rootCrumbs -= k - 1;
                break;
            }
        }

        // Moved behind dust relative to the first fixed corner → pin where the root last was,
        // so the vine bends around the obstacle instead of cutting over it.
        Vector2 firstFixed = route.nodes[1];
        Vector2Int firstCell = drum.WorldToGridPosition(firstFixed);
        if (!TentacleVinePlanner.SegmentClear(drum, starPos, firstFixed, starCell, firstCell, cell) &&
            Vector2.Distance(tentacle.lastRootPos, firstFixed) > cell * 0.25f &&
            Vector2.Distance(tentacle.lastRootPos, starPos) > 1e-4f)
        {
            route.InsertAfterRoot(tentacle.lastRootPos);
            tentacle.rootCrumbs++;
        }
        // Line of sight past a breadcrumb regained → that breadcrumb is slack.
        else if (tentacle.rootCrumbs > 0 && route.Count >= 3)
        {
            Vector2 second = route.nodes[2];
            if (Vector2.Distance(starPos, route.nodes[1]) > slack &&
                TentacleVinePlanner.SegmentClear(drum, starPos, second, starCell, drum.WorldToGridPosition(second), cell))
            {
                route.RemoveAfterRoot(1);
                tentacle.rootCrumbs--;
            }
        }

        route.SetRoot(starPos);
        route.FrontDistFromTarget = Mathf.Min(route.FrontDistFromTarget, route.TotalLength);
        tentacle.lastRootPos = starPos;
    }

    /// <summary>
    /// Advances the grow front along the route. When the next stretch of route is dust, the vine
    /// stalls, breaks that cell (normal StarBurrow regrow) and continues. Returns false when the
    /// vine is hard-blocked (Cosmic Block / uncarveable / another tentacle's target) past the timeout.
    /// </summary>
    private bool TickVineGrowth(Tentacle tentacle, float dt, DrumTrack drum, CosmicDustGenerator gen, Vector2 starPos)
    {
        var route = tentacle.route;
        if (route.Count < 2 || drum == null) { route.FrontDistFromTarget = 0f; return true; }

        float cell = CellWorld(drum);

        if (tentacle.boring)
        {
            tentacle.boreTimer += dt;
            bool stillSolid = drum.HasDustAt(tentacle.boreCell);
            if (!stillSolid && tentacle.boreTimer >= boreSeconds)
                tentacle.boring = false;
            else if (stillSolid && tentacle.boreTimer >= boreTimeout)
                return false;
            else
                return true; // stalled at the wall
        }

        // Probe half a cell ahead of the front for dust standing in the way (planned bore cells
        // and dust that regrew onto the route after planning are handled the same way).
        float grown = route.GrownLength;
        float probe = grown + cell * 0.5f;
        if (probe < route.TotalLength)
        {
            Vector2Int ahead = drum.WorldToGridPosition(route.Sample(probe));
            Vector2Int starCell = drum.WorldToGridPosition(starPos);
            if (ahead != tentacle.targetCell && ahead != starCell && drum.HasDustAt(ahead))
            {
                tentacle.boring = true;
                tentacle.boreCell = ahead;
                tentacle.boreTimer = 0f;

                bool breakable = gen != null && !gen.IsBlockAt(ahead) && !IsReservedByAnotherTentacle(ahead, tentacle);
                if (breakable)
                    gen.CarveCell(ahead, fadeSeconds: boreFadeSeconds, scheduleRegrow: true,
                        source: DustClearSource.StarBurrow, runPreExplode: true);
                return true;
            }
        }

        route.FrontDistFromTarget = Mathf.Max(0f, route.FrontDistFromTarget - tentacle.growUnitsPerSec * dt);
        return true;
    }
}
