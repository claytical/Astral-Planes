using System.Collections.Generic;
using UnityEngine;

// SuperNode-driven role dust removal: when this track is instantly filled to max (shard catch
// or the originating-track mirror fill), its role's CosmicDust is cleared and held out of the
// grid until this track's notes start ascending again, freeing overall grid capacity for other
// roles in the meantime.
public partial class InstrumentTrack
{
    private List<Vector2Int> _heldDustCellsFromFill;
    private bool _subscribedForDustRelease;

    // Called once per genuine note commit (not from AddNoteToLoop itself, which chord retunes
    // also call to re-quantize every already-committed note — hooking there would re-clear
    // dust on every chord change instead of only on new commits).
    private void NotifyNoteCommittedForDust()
    {
        GameFlowManager.Instance?.dustGenerator?.PermanentlyClearOneCellForRole(assignedRole);
    }

    public void ClearRoleDustUntilAscension()
    {
        var dust = GameFlowManager.Instance?.dustGenerator;
        if (dust == null) return;

        var held = dust.ClearAndHoldRoleCells(assignedRole);
        if (held == null || held.Count == 0) return;

        _heldDustCellsFromFill ??= new List<Vector2Int>();
        _heldDustCellsFromFill.AddRange(held);

        if (!_subscribedForDustRelease)
        {
            _subscribedForDustRelease = true;
            NoteAscensionDirector.OnNoteReachedAscent += HandleNoteAscendedForDustRelease;
        }
    }

    private void HandleNoteAscendedForDustRelease(InstrumentTrack track, int step, Vector3 pos)
    {
        if (track != this) return;
        UnsubscribeFromDustReleaseAscension();

        if (_heldDustCellsFromFill == null || _heldDustCellsFromFill.Count == 0) return;
        GameFlowManager.Instance?.dustGenerator?.ReleaseHeldCells(_heldDustCellsFromFill, DustClearSource.TrackFilled);
        _heldDustCellsFromFill.Clear();
    }

    // Called from InstrumentTrack.Lifecycle.cs's OnDisable/OnDestroy so a torn-down track never
    // leaves a dangling static-event subscription.
    private void UnsubscribeFromDustReleaseAscension()
    {
        if (!_subscribedForDustRelease) return;
        _subscribedForDustRelease = false;
        NoteAscensionDirector.OnNoteReachedAscent -= HandleNoteAscendedForDustRelease;
    }
}
