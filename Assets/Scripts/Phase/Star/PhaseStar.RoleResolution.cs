using UnityEngine;

public partial class PhaseStar
{
    private void PrepareNextDirective()
    {
        Trace("PrepareNextDirective() begin");

        _cachedTrack = null;

        if (_drum == null) return;

        MusicalRole role = GetPlannedRoleForHighlightedShard();

        InstrumentTrack track = FindTrackByRole(role);
        if (track == null) return;

        _cachedTrack = track;
        PrimeZapRequirementForRole(role, track);
    }

    private InstrumentTrack FindTrackByRole(MusicalRole role)
    {
        ResolveGameFlowManager();
        var controller = _gfm?.controller;
        if (controller == null || controller.tracks == null) return null;

        var roleProfile = MusicalRoleProfileLibrary.GetProfile(role);
        if (roleProfile != null && roleProfile.configSelectionMode == RoleConfigSelectionMode.ByVoice)
        {
            // Cycle through voices: pick the first one whose current-target bin isn't allocated yet.
            // This ensures each ejection targets a different voice (A → C → E) rather than
            // always reusing voice 0 before the chord group is complete.
            foreach (var t in controller.tracks)
            {
                if (t == null || t.assignedRole != role) continue;
                if (!t.IsBinAllocated(t.GetBinCursor()))
                    return t;
            }
            // All voices are mid-burst — fall back to first match.
        }

        foreach (var t in controller.tracks)
            if (t != null && t.assignedRole == role)
                return t;

        return null;
    }

    private MusicalRole GetPlannedRoleForHighlightedShard()
    {
        // Stable role identity per star: once attuned, always plan/eject for that role.
        if (_attunedRole != MusicalRole.None) return _attunedRole;
        if (_previewRole != MusicalRole.None) return _previewRole;

        // Fallback must come from motif-authored active roles, not a hardcoded musical role.
        // Some motifs intentionally omit Bass.
        var motifRoles = _assignedMotif?.GetActiveRoles();
        if (motifRoles != null)
        {
            for (int i = 0; i < motifRoles.Count; i++)
            {
                var role = motifRoles[i];
                if (role == MusicalRole.None) continue;
                if (FindTrackByRole(role) != null) return role;
            }
        }

        // Last-resort: pick any track role currently available.
        ResolveGameFlowManager();
        var tracks = _gfm?.controller?.tracks;
        if (tracks != null)
        {
            for (int i = 0; i < tracks.Length; i++)
            {
                var t = tracks[i];
                if (t == null || t.assignedRole == MusicalRole.None) continue;
                return t.assignedRole;
            }
        }

        return MusicalRole.None;
    }

    /// <summary>
    /// 0 = satiated (zap requirement met). 1 = starving (no zaps yet).
    /// Used by PhaseStarMotion2D to scale drift speed.
    /// </summary>
    public float GetHungerLevel()
    {
        if (_previewRole == MusicalRole.None) return 1f;
        return 1f - ZapProgress01;
    }
}
