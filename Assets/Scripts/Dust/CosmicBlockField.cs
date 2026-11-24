using System.Collections.Generic;
using UnityEngine;

// =========================================================================
//  CosmicBlockField — role-wide Cosmic Block controller
//
//  Every time a ring completes (InstrumentTrack.OnBinFilled → BinRingController
//  → OnRingCompleted), the previous role's Cosmic Blocks revert and every solid
//  dust cell of the completed ring's role becomes a block, in place — moving
//  the maze's boundaries each ring and pushing the player onto another dust
//  type. The dust-side work (flagging, block form, ship-wide doorways so notes
//  and ships are never sealed off) lives in CosmicDustGenerator.Blocks.cs.
//
//  Rules:
//   • Swaps are coalesced to once per frame (a SuperNode fills several bins at
//     once — the last completed role wins).
//   • No new wall when no OTHER active role still has unfilled bins: walling the
//     only role that can still progress would deadlock the motif. Old blocks
//     still revert.
//   • The motif bridge starting reverts everything (HardStopRegrowthForBridge) and no
//     new wall goes up until the bridge ends — the final ring's completion lands in the
//     same frame the bridge starts, and a wall applied after the release would ignore
//     the bridge's dust fade. When the record starts spinning off, any dust still solid
//     is faded out so nothing outlives the record's exit animation.
//
//  "About to move" tell: while any track has a live collectable burst (its
//  completion is the next ring, i.e. the next swap), the standing blocks shimmer
//  — random cells flash toward white, denser and faster as the burst nears
//  completion. In-world, via the dust's own flash lane.
//
//  (Replaces the 11-16 note-driven version: one CosmicBlock prefab per recorded
//  note, origin-cell provenance, echo sparks, heartbeat pulses.)
// =========================================================================
public class CosmicBlockField : MonoBehaviour
{
    [Header("About-to-move shimmer")]
    [SerializeField] private bool shimmerEnabled = true;
    [SerializeField] private float shimmerIntervalSlow = 0.4f;
    [SerializeField] private float shimmerIntervalFast = 0.08f;
    [Tooltip("At full burst progress, flash this share of the blocked cells per wave.")]
    [SerializeField, Range(0f, 1f)] private float shimmerMaxShare = 0.15f;
    [SerializeField] private float shimmerFlashSeconds = 0.7f;
    [Tooltip("Most blocked cells flashing at once. Each flash animates that cell's particles for " +
             "shimmerFlashSeconds, so this bounds the shimmer's per-frame cost on big walls.")]
    [SerializeField] private int shimmerMaxConcurrent = 24;
    [SerializeField, Range(0f, 1f)] private float shimmerFloor01 = 0.15f;

    private MotifRingGlyphApplicator _boundApplicator;
    private bool _swapPending;
    private InstrumentTrack _pendingTrack;
    private float _nextShimmer;
    private float _nextWatchdog;
    private const float kWatchdogSeconds = 0.5f;
    private readonly List<Vector2Int> _shimmerScratch = new List<Vector2Int>(256);
    private readonly Queue<float> _shimmerFlashEnds = new Queue<float>(32);

    private CosmicDustGenerator Gen => GameFlowManager.Instance != null ? GameFlowManager.Instance.dustGenerator : null;

    public bool IsBlockAt(Vector2Int cell)
    {
        var gen = Gen;
        return gen != null && gen.IsBlockAt(cell);
    }

    private void Start()
    {
        var gfm = GameFlowManager.Instance;
        if (gfm != null) BindRingApplicator(gfm.GetMotifRingGlyphApplicator());
    }

    private void OnDestroy()
    {
        BindRingApplicator(null);
    }

    /// <summary>Idempotent. Called from Start and from BinRingController.Setup (the applicator may be assigned late).</summary>
    public void BindRingApplicator(MotifRingGlyphApplicator applicator)
    {
        if (_boundApplicator == applicator) return;
        if (_boundApplicator != null) _boundApplicator.OnRecordSpinOffStarted -= HandleRecordLeaving;
        _boundApplicator = applicator;
        if (_boundApplicator != null) _boundApplicator.OnRecordSpinOffStarted += HandleRecordLeaving;
    }

    /// <summary>A ring completed for <paramref name="track"/>'s role. Applied at end of frame.</summary>
    public void OnRingCompleted(InstrumentTrack track)
    {
        if (track == null || BridgeActive) return;
        _pendingTrack = track;
        _swapPending = true;
    }

    /// <summary>Reverts every block now (motif end / teardown).</summary>
    public void ReleaseAll()
    {
        _swapPending = false;
        _pendingTrack = null;
        Gen?.ReleaseRoleBlock();
    }

    // True from the moment the motif is complete (record docking, then the bridge) until the
    // next motif starts. No new walls while the players head for the record.
    private static bool BridgeActive
    {
        get
        {
            var gfm = GameFlowManager.Instance;
            return gfm != null && (gfm.BridgePending || gfm.GhostCycleInProgress || gfm.RecordReady);
        }
    }

    // The final ring's notes have landed and the record starts its exit animation: the walls
    // go with it, and any dust the bridge's staggered fade hasn't reached yet fades out now.
    private void HandleRecordLeaving()
    {
        ReleaseAll();
        Gen?.FadeOutRemainingSolidDust(0.3f);
    }

    private void LateUpdate()
    {
        if (!_swapPending) return;
        _swapPending = false;
        var track = _pendingTrack;
        _pendingTrack = null;

        var gen = Gen;
        if (gen == null) return;
        gen.ReleaseRoleBlock();

        if (track == null || track.assignedRole == MusicalRole.None) return;
        // The final ring completes in the same frame the bridge starts (and releases the walls):
        // walling now would make those cells immune to the bridge's dust fade.
        if (BridgeActive) return;
        if (_boundApplicator != null && _boundApplicator.IsRecordLeaving) return;
        if (!AnyOtherRoleCanProgress(track)) return;

        gen.ApplyRoleBlock(track.assignedRole);
    }

    // True when some OTHER active role's track still has an unfilled bin — i.e. walling this
    // role still leaves the player something to work on.
    private static bool AnyOtherRoleCanProgress(InstrumentTrack completed)
    {
        var gfm = GameFlowManager.Instance;
        var tracks = gfm != null && gfm.controller != null ? gfm.controller.tracks : null;
        if (tracks == null) return false;
        var motif = gfm.phaseTransitionManager != null ? gfm.phaseTransitionManager.currentMotif : null;
        var activeRoles = motif != null ? motif.GetActiveRoles() : null;

        foreach (var t in tracks)
        {
            if (t == null || t.assignedRole == MusicalRole.None) continue;
            if (t.assignedRole == completed.assignedRole) continue;
            if (activeRoles != null && !activeRoles.Contains(t.assignedRole)) continue;
            if (t.GetFilledBinCount() < Mathf.Max(1, t.maxLoopMultiplier)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // About-to-move shimmer
    // ------------------------------------------------------------------
    private void Update()
    {
        // Watchdog: keep every on-screen star, live note and ship reachable while walls stand
        // (a star or note can drift somewhere ships can't follow after the swap).
        if (Time.time >= _nextWatchdog)
        {
            _nextWatchdog = Time.time + kWatchdogSeconds;
            Gen?.EnsureTargetsReachable();
        }

        if (!shimmerEnabled || Time.time < _nextShimmer) return;
        var gen = Gen;
        if (gen == null || gen.RoleBlockedCells.Count == 0) { _nextShimmer = Time.time + shimmerIntervalSlow; return; }

        float progress = MaxLiveBurstProgress(out bool anyLive);
        if (!anyLive) { _nextShimmer = Time.time + shimmerIntervalSlow; return; }

        float intensity = Mathf.Lerp(shimmerFloor01, 1f, progress);
        _nextShimmer = Time.time + Mathf.Lerp(shimmerIntervalSlow, shimmerIntervalFast, intensity);

        _shimmerScratch.Clear();
        _shimmerScratch.AddRange(gen.RoleBlockedCells);
        int k = Mathf.Max(1, Mathf.RoundToInt(_shimmerScratch.Count * shimmerMaxShare * intensity));
        while (_shimmerFlashEnds.Count > 0 && _shimmerFlashEnds.Peek() <= Time.time) _shimmerFlashEnds.Dequeue();
        k = Mathf.Min(k, Mathf.Max(0, shimmerMaxConcurrent - _shimmerFlashEnds.Count));
        Color flash = Color.Lerp(new Color(0.85f, 0.85f, 0.9f, 1f), Color.white, intensity);
        for (int i = 0; i < k; i++)
            if (gen.TryFlashSolidCell(_shimmerScratch[Random.Range(0, _shimmerScratch.Count)], flash, shimmerFlashSeconds))
                _shimmerFlashEnds.Enqueue(Time.time + shimmerFlashSeconds);
    }

    private static float MaxLiveBurstProgress(out bool anyLive)
    {
        anyLive = false;
        float best = 0f;
        var gfm = GameFlowManager.Instance;
        var tracks = gfm != null && gfm.controller != null ? gfm.controller.tracks : null;
        if (tracks == null) return 0f;
        foreach (var t in tracks)
        {
            if (t == null || !t.TryGetLiveBurstProgress01(out float p)) continue;
            anyLive = true;
            if (p > best) best = p;
        }
        return best;
    }
}
