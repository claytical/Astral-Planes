using UnityEngine;

public partial class PhaseStar
{
    private bool _previewInitialized;
    private int _baseSortingOrder;

    /// <summary>Sorting order of the star's diamonds; tentacles draw just above it.</summary>
    /// <summary>Star sorting order when the base sprite hasn't reported one yet.</summary>
    public const int DefaultBaseSortingOrder = 2000;
    public int BaseSortingOrder => _baseSortingOrder != 0 ? _baseSortingOrder : DefaultBaseSortingOrder;

    private void EnsurePreviewRing()
    {
        if (_previewInitialized) return;
        _previewInitialized = true;

        BuildPreviewRing();
    }

    private void WireBinSource(DrumTrack drum)
    {
        _drum = drum;
        if (_drum == null) return;
        EnsurePreviewRing();
    }

    // (Re)builds the star body: an empty diamond pool, sized to the current note count. Diamonds
    // form as notes are caught (PhaseStar.DiamondBody.cs); until then the star is its tentacles.
    void BuildPreviewRing()
    {
        DestroyAllDiamonds();

        if (_baseSortingOrder == 0)
        {
            var baseSr = GetComponentInChildren<SpriteRenderer>(true);
            _baseSortingOrder = baseSr ? baseSr.sortingOrder : DefaultBaseSortingOrder;
        }

        if (visuals == null) return;

        var role = _previewRole;
        var track = role != MusicalRole.None ? FindTrackByRole(role) : null;
        _previewColor = role != MusicalRole.None ? ResolveRoleColor(role, track) : Color.gray;

        EnsureDiamondCount(DiamondPoolSize());
    }
}
