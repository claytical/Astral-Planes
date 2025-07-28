using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/Spawn Strategy Profile")]
public class SpawnStrategyProfile : ScriptableObject
{
    public List<WeightedMineNode> mineNodes;

    public MinedObjectSpawnDirective GetMinedObjectDirective(
        InstrumentTrackController trackController,
        MusicalPhase currentPhase,
        MusicalPhaseProfile phaseProfile,
        MinedObjectPrefabRegistry objectRegistry,
        MineNodePrefabRegistry nodeRegistry)
    {
        List<WeightedMineNode> validNodes = new();

        foreach (var node in mineNodes)
        {
            // Check phase compatibility
            if (node.allowedPhases != null &&
                node.allowedPhases.Count > 0 &&
                !node.allowedPhases.Contains(currentPhase))
                continue;

            InstrumentTrack track = trackController.FindTrackByRole(node.role);
            if (!IsNodeUsefulForTrack(node, track, currentPhase, phaseProfile))
                continue;

            validNodes.Add(node);
        }

        if (validNodes.Count == 0)
        {
            Debug.LogWarning($"⚠️ No valid spawn nodes for {currentPhase}. Returning null.");
            return null;
        }

        WeightedMineNode selected = ChooseRandomWeighted(validNodes);

        InstrumentTrack selectedTrack = trackController.FindTrackByRole(selected.role);
        Color color = ShardColorUtility.RoleColor(selected.role);

        return selected.ToDirective(selectedTrack, color, nodeRegistry, objectRegistry);
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
            var expectedSeries = profile.GetNoteSetSeriesForRole(node.role);
            return node.noteSetSeries == expectedSeries;
        }

        if (node.minedObjectType == MinedObjectType.TrackUtility)
        {
            return track.IsTrackUtilityRelevant(node.trackModifierType);
        }

        return true;
    }

    private WeightedMineNode ChooseRandomWeighted(List<WeightedMineNode> nodes)
    {
        int totalWeight = nodes.Sum(n => n.weight);
        int pick = Random.Range(0, totalWeight);
        int cumulative = 0;

        foreach (var node in nodes)
        {
            cumulative += node.weight;
            if (pick < cumulative)
                return node;
        }

        return nodes[Random.Range(0, nodes.Count)]; // fallback
    }
}
