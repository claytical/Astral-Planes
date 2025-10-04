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
    

public MinedObjectSpawnDirective GetMinedObjectDirective(
    InstrumentTrackController trackController,
    MusicalPhase currentPhase,
    MusicalPhaseProfile phaseProfile,
    MinedObjectPrefabRegistry objectRegistry,
    MineNodePrefabRegistry nodeRegistry,
    NoteSetFactory noteSetFactory)
{
    if (trackController == null || mineNodes == null || mineNodes.Count == 0)
    {
        Debug.LogWarning("[SpawnStrategy:A] Missing controller or empty node list.");
        return null;
    }

    // --- Authoritative phase is passed in (call site should pass PTM.currentPhase)
    var phase = currentPhase;

    // --- Build candidate set (Option A: ALL nodes require explicit allowedPhases opt-in) ---
    List<(int idx, WeightedMineNode node)> valid = new();
    int skippedPhase = 0, skippedRole = 0, skippedUsefulness = 0;

    for (int i = 0; i < mineNodes.Count; i++)
    {
        var node = mineNodes[i];
        if (node == null) continue;

        // Strict phase gating for EVERYTHING (spawners & utilities)
        var list = node.allowedPhases;
        if (list == null || list.Count == 0 || !list.Contains(phase))
        {
            skippedPhase++;
            continue;
        }

        var track = trackController.FindTrackByRole(node.role);
        if (track == null)
        {
            skippedRole++;
            continue;
        }

        // Keep your existing usefulness logic (handles utilities vs spawners)
        if (!IsNodeUsefulForTrack(node, track, phase, phaseProfile))
        {
            skippedUsefulness++;
            continue;
        }

        valid.Add((i, node));
    }

    if (valid.Count == 0)
    {
        Debug.LogWarning($"[SpawnStrategy:A] 0 candidates for phase={phase} " +
                         $"(phase-gate={skippedPhase}, role-miss={skippedRole}, not-useful={skippedUsefulness}).");
        return null; // let caller decide fallback/advance
    }

    // --- PICK ACCORDING TO MODE ---
    (int idx, WeightedMineNode pick) chosen = selectionMode switch
    {
        MineNodeSelectionMode.RoundRobinUniqueFirst => PickRoundRobinUniqueFirst(valid),
        MineNodeSelectionMode.QuotaBased           => PickQuotaBased(valid),
        _                                          => PickWeighted(valid)
    };

    if (chosen.pick == null)
    {
        Debug.LogWarning("[SpawnStrategy:A] Selection produced null; falling back to weighted.");
        chosen = PickWeighted(valid);
        if (chosen.pick == null) return null;
    }

    // Consume any quota/counters
    ConsumeQuota(chosen.idx);

    // --- Build directive without doing side-effects on the Track ---
    var selectedTrack = trackController.FindTrackByRole(chosen.pick.role);
    if (selectedTrack == null)
    {
        Debug.LogError("[SpawnStrategy:A] Selected track is null.");
        return null;
    }

    NoteSet generatedNoteSet = null;

    if (chosen.pick.minedObjectType == MinedObjectType.NoteSpawner)
    {
        if (noteSetFactory == null)
        {
            Debug.LogError("[SpawnStrategy:A] NoteSetFactory is null for NoteSpawner.");
            return null;
        }

        // Generate a NoteSet for the directive, but DO NOT call selectedTrack.SetNoteSet() here.
        // Let the spawn/collect pipeline apply it at the correct moment.
        generatedNoteSet = noteSetFactory.Generate(selectedTrack, phase);
    }
    // else: for TrackUtility, there is no NoteSet to generate here.

    var directive = chosen.pick.ToDirective(
        selectedTrack,
        generatedNoteSet,
        selectedTrack.trackColor,
        nodeRegistry,
        objectRegistry,
        noteSetFactory,
        phase
    );

    Debug.Log($"[SpawnStrategy:A] phase={phase} pick={chosen.pick} track={selectedTrack.name} type={chosen.pick.minedObjectType}");
    return directive;
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
            acc += n.node.weight;
            if (pick < acc) return n;
        }
        return withQuota[Random.Range(0, withQuota.Count)];
    }

    public void ResetForNewPhase()
    {
        _rrCursor = 0;
        _rrUsedThisStar = new HashSet<int>();
        _quotaRemaining = new Dictionary<int,int>();
        for (int i = 0; i < mineNodes.Count; i++)
        {
            var q = Mathf.Max(0, mineNodes[i].quota);
            if (q > 0) _quotaRemaining[i] = q;
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
