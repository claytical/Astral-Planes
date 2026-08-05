using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One tunnel mouth in a path-choice interstitial maze. Passive data holder + trigger
/// detector; PathChoiceCoordinator builds/positions the GameObject and collider, then calls
/// Configure(). Firing OnTriggerEnter2D just reports back to the owner — winner selection,
/// sealing, and redirect all live in PathChoiceCoordinator.
/// </summary>
public class MazePathExit : MonoBehaviour
{
    public Vector2Int AnchorCell { get; private set; }
    public IReadOnlyList<Vector2Int> GapCells { get; private set; }
    public BoundaryWrap.BoundarySide Side { get; private set; }
    public int? TargetMotifIndex { get; private set; }
    public int? TargetPhaseIndex { get; private set; }

    private PathChoiceCoordinator _owner;
    private Collider2D _col;
    private GameObject _visualInstance;

    public void Configure(
        PathChoiceCoordinator owner,
        Vector2Int anchorCell,
        List<Vector2Int> gapCells,
        BoundaryWrap.BoundarySide side,
        int? targetMotifIndex,
        int? targetPhaseIndex,
        GameObject visualInstance = null)
    {
        _owner = owner;
        AnchorCell = anchorCell;
        GapCells = gapCells;
        Side = side;
        TargetMotifIndex = targetMotifIndex;
        TargetPhaseIndex = targetPhaseIndex;
        _col = GetComponent<Collider2D>();
        _visualInstance = visualInstance;
    }

    public void SetSealed(bool sealedShut)
    {
        if (_col != null) _col.enabled = !sealedShut;
        // Hide the exit's particle effect the instant it seals, so a losing tunnel's marker
        // doesn't keep playing after PathChoiceCoordinator has already resealed it with solid dust.
        if (_visualInstance != null) _visualInstance.SetActive(!sealedShut);
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        var rb = other.attachedRigidbody;
        if (rb == null) return;

        var vehicle = rb.GetComponentInParent<Vehicle>();
        if (vehicle != null) _owner?.OnVehicleReachedExit(this, vehicle);
    }
}
