using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

[CreateAssetMenu(menuName = "Astral Planes/Spawn Strategy Profile")]
public class SpawnStrategyProfile : ScriptableObject
{
    public List<WeightedMineNode> mineNodes;
    public MineNodeSelectionMode selectionMode = MineNodeSelectionMode.WeightedRandom;
    // --- runtime (not serialized) ---
    [NonSerialized] private int _rrCursor;                  // round-robin cursor
    [NonSerialized] private HashSet<int> _rrUsedThisStar ;// “unique-first” set
    [NonSerialized] private Dictionary<int,int> _quotaRemaining;// per-node quotas (by index)
    

// SpawnStrategyProfile.cs
  public MinedObjectSpawnDirective GetMinedObjectDirective(
        InstrumentTrackController trackController,
        MusicalPhase currentPhase,
        MusicalPhaseProfile phaseProfile,
        MinedObjectPrefabRegistry objectRegistry,
        MineNodePrefabRegistry nodeRegistry,
        NoteSetFactory noteSetFactory,
        HashSet<MusicalRole> avoidRoles = null // <-- NEW
    )
    {
        if (trackController == null || nodeRegistry == null || objectRegistry == null)
            return null;

        // Build candidate list with original indices so the caller can keep quotas/round-robin if needed
        var candidates = new List<(int idx, WeightedMineNode node)>(mineNodes.Count);

        for (int i = 0; i < mineNodes.Count; i++)
        {
            var node = mineNodes[i];

            // Phase gating
            if (node.allowedPhases != null && node.allowedPhases.Count > 0 &&
                !node.allowedPhases.Contains(currentPhase))
                continue;

            // Role avoidance: skip any item whose role is in the avoid set
            if (avoidRoles != null && avoidRoles.Contains(node.role))
                continue;

            // Track must exist for this role (don’t assume FindTrackByRole exists)
            var track = trackController.tracks?.FirstOrDefault(
                t => t != null && t.assignedRole == node.role
            );
            if (track == null)
                continue;

            // Keep your existing usefulness checks if you have them (utility eligibility, etc.)
            if (!IsNodeUsefulForTrack(node, track, currentPhase, phaseProfile))
                continue;

            // If it passed all filters, it’s a valid candidate
            candidates.Add((i, node));
        }

        // If we filtered everything out, relax the avoidRoles once (so we still spawn something)
        if (candidates.Count == 0)
        {
            for (int i = 0; i < mineNodes.Count; i++)
            {
                var node = mineNodes[i];

                if (node.allowedPhases != null && node.allowedPhases.Count > 0 &&
                    !node.allowedPhases.Contains(currentPhase))
                    continue;

                var track = trackController.tracks?.FirstOrDefault(
                    t => t != null && t.assignedRole == node.role
                );
                if (track == null) continue;
                if (!IsNodeUsefulForTrack(node, track, currentPhase, phaseProfile)) continue;

                candidates.Add((i, node));
            }
            if (candidates.Count == 0) return null;
        }

        // Weighted pick (your function)
        var picked = PickWeighted(candidates);

        // Resolve the actual track object
        var pickedTrack = trackController.tracks?.FirstOrDefault(
            t => t != null && t.assignedRole == picked.node.role
        );
        if (pickedTrack == null) return null;

        // Build the directive the same way you currently do (prefer your existing ToDirective)
        var dir = picked.node.ToDirective(
            pickedTrack,
            /* noteSet: */ null,                     // let ToDirective / factory decide, or fill below
            pickedTrack.trackColor,
            nodeRegistry,
            objectRegistry,
            noteSetFactory,
            currentPhase
        );

        // Attach the MusicalRoleProfile so instrument-specific behavior is preserved
        var roleProfile = MusicalRoleProfileLibrary.GetProfile(pickedTrack.assignedRole);
        if (dir != null) dir.roleProfile = roleProfile; // assumes directive has this field

        return dir;
    }
    private (int idx, WeightedMineNode node) PickWeighted(List<(int idx, WeightedMineNode node)> nodes)
    {
        // your existing weighted logic
        int total = nodes.Sum(n => n.node.weight);
        int pick = Random.Range(0, Mathf.Max(1, total));
        int acc = 0;
        foreach (var n in nodes)
        {
            acc += n.node.weight;
            if (pick < acc) return n;
        }
        return nodes[Random.Range(0, nodes.Count)];
    }

    private (int idx, WeightedMineNode node) PickRoundRobinUniqueFirst(List<(int idx, WeightedMineNode node)> nodes)
    {
        _rrUsedThisStar ??= new HashSet<int>();
        // 1) try to find a valid node we haven't used during this star
        int n = nodes.Count;
        Debug.Log($"Nodes: {nodes}");
        for (int step = 0; step < n; step++)
        {
            var k = (_rrCursor + step) % n;
            var candidate = nodes[k];
            if (!_rrUsedThisStar.Contains(candidate.idx))
            {
                _rrCursor = (k + 1) % n;
                _rrUsedThisStar.Add(candidate.idx);
                return candidate;
            }
        }
        Debug.Log($"Fallback to weighted from Round Robin Unique First.");
        // 2) everybody used once → fall back to weighted among valid
        return PickWeighted(nodes);
    }

    private (int idx, WeightedMineNode node) PickQuotaBased(List<(int idx, WeightedMineNode node)> nodes)
    {
        // Prefer nodes with remaining quota; if none, fall back to weighted
        var withQuota = nodes.Where(n => HasQuota(n.idx)).ToList();
        if (withQuota.Count == 0) return PickWeighted(nodes);

        // Optional: still respect weights inside the quota subset
        int total = withQuota.Sum(n => n.node.weight);
        int pick = Random.Range(0, Mathf.Max(1, total));
        int acc = 0;
        foreach (var n in withQuota)
        {
            Debug.Log($"Adding {n.node.weight} to {n.node}");
            acc += n.node.weight;
            if (pick < acc) return n;
        }
        return withQuota[Random.Range(0, withQuota.Count)];
    }

    public void ResetForNewPhase()
    {
        _rrCursor = 0;
        _rrUsedThisStar = new HashSet<int>();

        if (_quotaRemaining == null)
            _quotaRemaining = new Dictionary<int, int>(mineNodes.Count);
        else
            _quotaRemaining.Clear();

        for (int i = 0; i < mineNodes.Count; i++)
        {
            int q = mineNodes[i].quota;

            if (q < 0)
                continue; // disabled
            else if (q == 0)
                _quotaRemaining[i] = int.MaxValue; // unlimited
            else
                _quotaRemaining[i] = q;
        }
    }

    // Call each time a PhaseStar spawns (start of a star life)
    public void BeginStarCycle()
    {
        // keep quotas, but reset the “unique-first” pass for this star
        _rrUsedThisStar = new HashSet<int>();
    }

    private bool HasQuota(int idx)
        => _quotaRemaining != null && _quotaRemaining.TryGetValue(idx, out var left) && left > 0;

    private void ConsumeQuota(int idx)
    {
        if (_quotaRemaining == null) return;
        if (_quotaRemaining.TryGetValue(idx, out var left))
        {
            left = Mathf.Max(0, left - 1);
            if (left == 0) _quotaRemaining.Remove(idx);
            else _quotaRemaining[idx] = left;
        }
    }

    private bool IsNodeUsefulForTrack(
        WeightedMineNode node,
        InstrumentTrack track,
        MusicalPhase phase,
        MusicalPhaseProfile profile)
    {
        if (track == null)
            return false;

        if (node.minedObjectType == MinedObjectType.NoteSpawner)
        {
            //var expectedSeries = profile.GetNoteSetSeriesForRole(node.role);
            //return node.noteSetSeries == expectedSeries;
            return node.role == track.assignedRole;
        }

        if (node.minedObjectType == MinedObjectType.TrackUtility)
        {
            return track.IsTrackUtilityRelevant(node.trackModifierType);
        }

        return true;
    }

}
