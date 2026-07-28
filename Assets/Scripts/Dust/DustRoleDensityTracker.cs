using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// Tracks how many Solid cells are currently imprinted with each role voice and resolves
/// which voice a cell should regrow as (imprint / hidden Voronoi imprint / neighbor plurality /
/// global least-dense, in that precedence order). Counts are updated by the cell registry
/// whenever a cell's Solid state flips.
///
/// Internally keyed by RoleVoiceKey (role + voiceIndex) so a motif can declare 2+ voices of the
/// same MusicalRole, each with independent territory/density. The MusicalRole-only public surface
/// (ResolveRegrowRole, IsRoleActive, ActiveRoles) is preserved as thin delegations to the voice0
/// case for existing callers — every currently-shipped motif has exactly one voice per role, so
/// behavior is unchanged for them.
/// </summary>
public sealed class DustRoleDensityTracker
{
    private readonly DustImprintStore _imprints;
    private readonly DustGridState _gridState;
    private readonly Func<Vector2Int, bool> _hasForceGrayRegrowFlag;
    private readonly Func<Vector2Int, bool> _isCellSolid;
    private readonly Func<Vector2Int, CosmicDust> _tryGetDustAt;

    // Cell -> role that should be excluded from regrow resolution (currently never written
    // anywhere in the repo, so this exclusion branch is a permanent no-op today; carried
    // over verbatim rather than fixed/removed — see project memory for the flagged follow-up).
    private readonly Dictionary<Vector2Int, MusicalRole> _regrowExcludeRoleByCell = new Dictionary<Vector2Int, MusicalRole>(2048);

    private readonly Dictionary<RoleVoiceKey, int> _solidCountByVoice = new Dictionary<RoleVoiceKey, int>();

    private List<MusicalRole> _activeRoles;   // set from motif at phase start via SetActiveRoles
    private List<RoleVoiceKey> _activeVoices; // set from motif at phase start via SetActiveVoices

    // Motif-assigned profile per voice. MusicalRoleProfileLibrary keeps only an arbitrary
    // alphabetically-first profile per role, which can't reflect which profile variant the
    // current motif actually assigned to a given role/voice — this cache is the authoritative source.
    private readonly Dictionary<RoleVoiceKey, MusicalRoleProfile> _roleProfileByVoice = new Dictionary<RoleVoiceKey, MusicalRoleProfile>();

    public IReadOnlyList<MusicalRole> ActiveRoles => _activeRoles;
    public IReadOnlyList<RoleVoiceKey> ActiveVoices => _activeVoices;

    public DustRoleDensityTracker(
        DustImprintStore imprints,
        DustGridState gridState,
        Func<Vector2Int, bool> hasForceGrayRegrowFlag,
        Func<Vector2Int, bool> isCellSolid,
        Func<Vector2Int, CosmicDust> tryGetDustAt)
    {
        _imprints = imprints;
        _gridState = gridState;
        _hasForceGrayRegrowFlag = hasForceGrayRegrowFlag;
        _isCellSolid = isCellSolid;
        _tryGetDustAt = tryGetDustAt;
    }

    private static List<RoleVoiceKey> DefaultVoices() => new List<RoleVoiceKey>
    {
        MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove, MusicalRole.Rhythm
    };

    public void SetActiveRoles(IReadOnlyList<MusicalRole> roles)
    {
        _activeRoles = roles != null && roles.Count > 0
            ? new List<MusicalRole>(roles)
            : new List<MusicalRole> { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove, MusicalRole.Rhythm };
    }

    // Populates active voices + the voice->profile cache from the motif's actual
    // RoleMotifNoteSetConfig assignments. Call alongside SetActiveRoles at phase start.
    public void SetActiveVoices(MotifProfile motif)
    {
        var voices = motif?.GetActiveVoices();
        _activeVoices = voices != null && voices.Count > 0
            ? new List<RoleVoiceKey>(voices)
            : DefaultVoices();

        _roleProfileByVoice.Clear();
        if (motif == null) return;
        for (int i = 0; i < _activeVoices.Count; i++)
        {
            var v = _activeVoices[i];
            var cfg = motif.GetConfigForRoleAtBin(v.role, 0, 1, v.voiceIndex);
            if (cfg?.roleProfile != null) _roleProfileByVoice[v] = cfg.roleProfile;
        }
    }

    public void RemoveExcludedRole(Vector2Int gp) => _regrowExcludeRoleByCell.Remove(gp);

    // Prefers the motif-assigned profile for this voice; falls back to the library's arbitrary
    // first-loaded profile only when no motif has configured this role/voice yet (e.g. before
    // phase start). MusicalRole arguments implicitly convert to voice 0.
    public MusicalRoleProfile GetResolvedRoleProfile(RoleVoiceKey key)
    {
        if (_roleProfileByVoice.TryGetValue(key, out var p) && p != null) return p;
        return MusicalRoleProfileLibrary.GetProfile(key.role);
    }

    public MusicalRoleProfile GetResolvedRoleProfile(MusicalRole role) => GetResolvedRoleProfile(new RoleVoiceKey(role, 0));

    /// <summary>
    /// Call when a cell's Solid state changes so voice density stays current.
    /// </summary>
    public void TrackRoleDensityChange(Vector2Int gp, bool becomesSolid)
    {
        MusicalRole role = MusicalRole.None;
        int voiceIndex = 0;
        if (_imprints != null && _imprints.TryGetValue(gp, out var imp))
        {
            role = imp.role;
            voiceIndex = imp.voiceIndex;
        }
        // Also check the live dust object in case the imprint was removed but Role was set.
        if (role == MusicalRole.None)
        {
            var d = _tryGetDustAt(gp);
            if (d != null) { role = d.Role; voiceIndex = d.VoiceIndex; }
        }

        if (role == MusicalRole.None) return;

        var key = new RoleVoiceKey(role, voiceIndex);
        int current = _solidCountByVoice.TryGetValue(key, out var c) ? c : 0;
        _solidCountByVoice[key] = Mathf.Max(0, current + (becomesSolid ? 1 : -1));
    }

    /// <summary>
    /// Returns the active voice with the fewest solid cells.
    /// Falls back to a random voice if all counts are equal (avoids deterministic bias).
    /// </summary>
    private RoleVoiceKey GetLeastDenseVoice()
    {
        var voices = (_activeVoices != null && _activeVoices.Count > 0)
            ? new List<RoleVoiceKey>(_activeVoices)
            : DefaultVoices();

        RoleVoiceKey best = voices.Count > 0 ? voices[0] : new RoleVoiceKey(MusicalRole.Bass, 0);
        int bestCount = int.MaxValue;

        // Shuffle order for tie-breaking
        for (int i = voices.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (voices[i], voices[j]) = (voices[j], voices[i]);
        }
        foreach (var v in voices)
        {
            int cnt = _solidCountByVoice.TryGetValue(v, out var c) ? c : 0;
            if (cnt < bestCount) { bestCount = cnt; best = v; }
        }
        return best;
    }

    private RoleVoiceKey GetLeastDenseVoiceExcluding(RoleVoiceKey excluded)
    {
        RoleVoiceKey best = new RoleVoiceKey(MusicalRole.None, 0);
        int bestCount = int.MaxValue;

        var voices = (_activeVoices != null && _activeVoices.Count > 0)
            ? (IReadOnlyList<RoleVoiceKey>)_activeVoices
            : DefaultVoices();

        foreach (var v in voices)
        {
            if (v.Equals(excluded)) continue;
            int cnt = _solidCountByVoice.TryGetValue(v, out var c) ? c : 0;
            if (cnt < bestCount)
            {
                bestCount = cnt;
                best = v;
            }
            else if (cnt == bestCount && Random.value > 0.5f)
            {
                best = v; // random tie-break
            }
        }

        // If excluded was the only active voice, ignore the exclusion rather than
        // returning None or a voice outside the motif's active set.
        if (best.role == MusicalRole.None)
            best = GetLeastDenseVoice();

        return best;
    }

    public int TotalSolidCount()
    {
        // _gridState.AllSolidCount tracks ALL solid cells including MusicalRole.None.
        // _solidCountByVoice only tracks non-None roles, so it can't be used here
        // after the gray-start change where all initial cells have role=None.
        return _gridState.AllSolidCount;
    }

    public MusicalRole ResolveRegrowRole(Vector2Int gp) => ResolveRegrowVoice(gp).role;

    public RoleVoiceKey ResolveRegrowVoice(Vector2Int gp)
    {
        RoleVoiceKey excludedVoice = new RoleVoiceKey(MusicalRole.None, 0);
        if (_regrowExcludeRoleByCell.TryGetValue(gp, out var ex))
            excludedVoice = new RoleVoiceKey(ex, 0);

        if (_imprints != null && _imprints.TryGetValue(gp, out var existingImp))
        {
            if (existingImp.role == MusicalRole.None)
            {
                // A non-vehicle carve requested the cell stay gray: regrow gray, full stop.
                // Falling through to neighbor/density here would re-tint (and re-charge)
                // cells the player never earned, feeding stars from untouched dust.
                if (_hasForceGrayRegrowFlag(gp))
                    return new RoleVoiceKey(MusicalRole.None, 0);

                // Gray-start cell: consult hidden imprint before giving up.
                var hiddenVoice = new RoleVoiceKey(existingImp.hiddenRole, existingImp.hiddenVoiceIndex);
                if (existingImp.hiddenRole != MusicalRole.None && IsVoiceActive(hiddenVoice))
                    return hiddenVoice;
                // No active hidden imprint — fall through to neighbor/density logic.
            }
            else
            {
                var existingVoice = new RoleVoiceKey(existingImp.role, existingImp.voiceIndex);
                if (IsVoiceActive(existingVoice))
                    return existingVoice;
            }
        }

        // No imprint or imprint voice is inactive: fall back to neighbor plurality / least dense.
        // Force-gray cells never take a fallback role.
        if (_hasForceGrayRegrowFlag(gp))
            return new RoleVoiceKey(MusicalRole.None, 0);

        var neighborVoice = GetPluralityNeighborVoice(gp, excludedVoice);
        return neighborVoice.role != MusicalRole.None ? neighborVoice : GetLeastDenseVoiceExcluding(excludedVoice);
    }

    /// <summary>
    /// Returns the voice held by the plurality of solid imprinted neighbors within 1 cell.
    /// Ties broken by global density (least-dense wins). Returns None when no imprinted
    /// solid neighbors exist.
    /// </summary>
    private RoleVoiceKey GetPluralityNeighborVoice(Vector2Int cell, RoleVoiceKey excluded)
    {
        if (_imprints == null) return new RoleVoiceKey(MusicalRole.None, 0);

        var counts = new Dictionary<RoleVoiceKey, int>();
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            var n = new Vector2Int(cell.x + dx, cell.y + dy);
            if (!_isCellSolid(n)) continue;
            if (!_imprints.TryGetValue(n, out var imp) || imp.role == MusicalRole.None) continue;

            var key = new RoleVoiceKey(imp.role, imp.voiceIndex);
            if (key.Equals(excluded) || !IsVoiceActive(key)) continue;

            counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
        }

        if (counts.Count == 0) return new RoleVoiceKey(MusicalRole.None, 0);

        int max = 0;
        foreach (var kv in counts)
            if (kv.Value > max) max = kv.Value;

        // Among tied leaders, prefer the globally least-dense (secondary balance signal).
        RoleVoiceKey best = new RoleVoiceKey(MusicalRole.None, 0);
        int bestDensity = int.MaxValue;
        foreach (var kv in counts)
        {
            if (kv.Value != max) continue;
            int d = _solidCountByVoice.TryGetValue(kv.Key, out var cv) ? cv : 0;
            if (d < bestDensity) { bestDensity = d; best = kv.Key; }
        }
        return best;
    }

    // Returns true if the role is present in the current motif's active role list.
    // When no active roles are set (fallback), all roles are considered active.
    public bool IsRoleActive(MusicalRole role)
    {
        if (_activeRoles == null || _activeRoles.Count == 0) return true;
        return _activeRoles.Contains(role);
    }

    // Returns true if the voice is present in the current motif's active voice list.
    // When no active voices are set (fallback), all voices are considered active.
    public bool IsVoiceActive(RoleVoiceKey key)
    {
        if (_activeVoices == null || _activeVoices.Count == 0) return true;
        return _activeVoices.Contains(key);
    }
}
