using System;
using System.Collections.Generic;
using System.Linq;
using Gameplay.Mining;
using UnityEngine;
using Random = UnityEngine.Random;

[CreateAssetMenu(menuName = "Astral Planes/Spawn Strategy Profile")]
public class SpawnStrategyProfile : ScriptableObject
{
    [Tooltip("Mixed list of NoteSpawner and TrackUtility entries.")]
    public List<WeightedMineNode> mineNodes;


    // --- runtime (not serialized) ---
    [NonSerialized] private int _rrCursor;                       // round-robin cursor
    [NonSerialized] private HashSet<int> _rrUsedThisStar;        // “unique-first” set
    [NonSerialized] private Dictionary<int,int> _quotaRemaining; // per-node quotas (by index)
    [NonSerialized] private bool _loggedNoteSpawnerWithModifier; // one-time warning

    #region Public API (PhaseStar should call these)

    /// <summary>Strict phase filter. Spawners ignore TrackModifierType.</summary>
    public IEnumerable<WeightedMineNode> GetCandidates(MusicalPhase phase) =>
        (mineNodes ?? s_empty).Where(n => n != null
                                       && n.allowedPhases != null
                                       && n.allowedPhases.Contains(phase));

    public IEnumerable<WeightedMineNode> GetCandidates(MusicalPhase phase, MinedObjectType type) =>
        GetCandidates(phase).Where(n => n.minedObjectType == type);

    /// <summary>
    /// Pick the next plan item for this star.
    /// - Phase is strict.
    /// - NoteSpawner ignores TrackModifierType (warns once if set).
    /// - Optional utilityBias (0..1) raises probability of TrackUtility picks.
    /// </summary>
    public WeightedMineNode PickNext(MusicalPhase phase, float utilityBias01 = -1f)
    {
        var cands = GetCandidates(phase).ToList();
        if (cands.Count == 0) return null;

        // sanitize: NoteSpawner must not rely on modifier
        if (!_loggedNoteSpawnerWithModifier)
        {
            foreach (var c in cands)
            {
                if (c.minedObjectType == MinedObjectType.NoteSpawner && c.trackModifierType != TrackModifierType.Clear /* any value */)
                {
                    Debug.LogWarning($"[SpawnStrategyProfile] NoteSpawner entry has a TrackModifierType ({c.trackModifierType}) which will be IGNORED.");
                    _loggedNoteSpawnerWithModifier = true;
                    break;
                }
            }
        }

        if (utilityBias01 >= 0f)
            return PickWithUtilityBias(cands, Mathf.Clamp01(utilityBias01));

        return PickWeightedRandom(cands);
    }

    /// <summary>
    /// Build a directive shell from a chosen entry.
    /// NOTE: This does NOT look up prefabs. PhaseStar should set:
    /// - directive.prefab (MineNode shell from PhaseStar)
    /// - directive.minedObjectPrefab (from DrumTrack's MinedObjectPrefabRegistry)
    /// - directive.noteSet for NoteSpawner (via GameFlowManager.noteSetFactory)
    /// - directive.displayColor = track.trackColor
    /// </summary>
    public MinedObjectSpawnDirective ToDirectiveShell(WeightedMineNode node, InstrumentTrack track)
    {
        if (node == null || track == null) return null;

        var d = new MinedObjectSpawnDirective
        {
            minedObjectType   = node.minedObjectType,
            assignedTrack     = track,
            displayColor      = track.trackColor, // ← always inherit from track
            remixUtility      = null,
            noteSet           = null
        };

        // For utilities, carry the modifier; for spawners: ignore it.
        if (node.minedObjectType == MinedObjectType.TrackUtility)
            d.trackModifierType = node.trackModifierType;

        return d;
    }

    /// <summary>Call when a new PhaseStar is spawned to reset round-robin state.</summary>
    public void ResetForNewStar()
    {
        _rrCursor = 0;
        _rrUsedThisStar = new HashSet<int>();
        _quotaRemaining = null; // lazily allocated if you add quotas per entry
    }

    #endregion

    #region Pickers

    private WeightedMineNode PickWeightedRandom(List<WeightedMineNode> cands)
    {
        int total = 0;
        for (int i = 0; i < cands.Count; i++)
            total += Mathf.Max(1, cands[i].weight);

        int roll = Random.Range(0, Mathf.Max(1, total));
        int sum  = 0;
        for (int i = 0; i < cands.Count; i++)
        {
            sum += Mathf.Max(1, cands[i].weight);
            if (roll < sum) return cands[i];
        }
        return cands[cands.Count - 1];
    }

    private WeightedMineNode PickRoundRobin(List<WeightedMineNode> cands)
    {
        if (cands.Count == 0) return null;
        var idx = Mathf.Abs(_rrCursor) % cands.Count;
        _rrCursor++;
        return cands[idx];
    }

    private WeightedMineNode PickRoundRobinUnique(List<WeightedMineNode> cands)
    {
        if (cands.Count == 0) return null;

        // produce a stable view (index into original list)
        var indexed = cands.Select((n, i) => (Node:n, i)).ToList();

        for (int k = 0; k < indexed.Count; k++)
        {
            var (n, i) = indexed[(_rrCursor + k) % indexed.Count];
            if (_rrUsedThisStar.Contains(i)) continue;
            _rrUsedThisStar.Add(i);
            _rrCursor = (_rrCursor + k + 1) % indexed.Count;
            return n;
        }

        // all used → reset unique set and return next
        _rrUsedThisStar.Clear();
        return PickRoundRobin(cands);
    }

    /// <summary>
    /// Utility bias: raise probability of TrackUtility relative to NoteSpawner.
    /// bias=0 → behave like WeightedRandom over all; bias=1 → prefer utilities if any exist.
    /// </summary>
    private WeightedMineNode PickWithUtilityBias(List<WeightedMineNode> cands, float bias)
    {
        var spawners = cands.Where(n => n.minedObjectType == MinedObjectType.NoteSpawner).ToList();
        var utils    = cands.Where(n => n.minedObjectType == MinedObjectType.TrackUtility).ToList();

        if (utils.Count == 0) return PickWeightedRandom(spawners.Count > 0 ? spawners : cands);
        if (spawners.Count == 0) return PickWeightedRandom(utils);

        // Decide the branch first, then pick weighted inside the branch
        bool chooseUtility = Random.value < bias;
        return chooseUtility ? PickWeightedRandom(utils) : PickWeightedRandom(spawners);
    }

    #endregion

    private static readonly List<WeightedMineNode> s_empty = new();

    // (Optional) Keep any per-track fit checks you had:
    public bool NodeFitsTrack(WeightedMineNode node, InstrumentTrack track)
    {
        if (node == null || track == null) return false;

        if (node.minedObjectType == MinedObjectType.NoteSpawner)
        {
            // Example policy: prefer node.role == assignedRole (if you keep role on entries)
            return node.role == track.assignedRole;
        }

        if (node.minedObjectType == MinedObjectType.TrackUtility)
        {
            // Utilities: ask track if this utility makes sense (your function)
            return track.IsTrackUtilityRelevant(node.trackModifierType);
        }

        return true;
    }
}

/// <summary>
/// Existing enum in your project:
/// WeightedRandom, RoundRobin, RoundRobinUnique, ...
/// </summary>
public enum MineNodeSelectionMode { WeightedRandom, RoundRobin, RoundRobinUnique }

/// <summary>
/// This class already exists in your project; keeping the same name to avoid asset breakage.
/// Only change is semantic: when type == NoteSpawner, trackModifierType is ignored.
/// </summary>
[Serializable]
public class WeightedMineNode
{
    public string label;

    public MinedObjectType minedObjectType = MinedObjectType.NoteSpawner;

    [Tooltip("Only used when minedObjectType == TrackUtility")]
    public TrackModifierType trackModifierType = TrackModifierType.RhythmStyle;

    public MusicalRole role;                      // optional: used by your NodeFitsTrack policy
    public List<MusicalPhase> allowedPhases = new List<MusicalPhase>();

    [Min(1)] public int weight = 1;
    public int quota = -1;                        // optional future use
    public int rarity = 0;                        // optional future use
}
