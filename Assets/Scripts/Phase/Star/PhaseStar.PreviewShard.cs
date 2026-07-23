using UnityEngine;

public partial class PhaseStar
{
    private bool _previewInitialized;
    private bool _buildingPreview;
    private int _baseSortingOrder;

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

    void BuildPreviewRing()
    {
        _buildingPreview = true;

        if (_previewVisual != null)
        {
            _previewVisual.SetParent(null);
            Destroy(_previewVisual.gameObject);
            _previewVisual = null;
        }

        if (_previewVisualB != null)
        {
            _previewVisualB.SetParent(null);
            Destroy(_previewVisualB.gameObject);
            _previewVisualB = null;
        }

        if (_baseSortingOrder == 0)
        {
            var baseSr = GetComponentInChildren<SpriteRenderer>(true);
            _baseSortingOrder = baseSr ? baseSr.sortingOrder : 2000;
        }

        if (visuals == null)
        {
            _buildingPreview = false;
            return;
        }

        // Role is determined at runtime via attunement; start gray until the star drains its first colored dust.
        var role = _previewRole;
        var track = role != MusicalRole.None ? FindTrackByRole(role) : null;
        Color roleColor = role != MusicalRole.None ? ResolveRoleColor(role, track) : Color.gray;
        _previewColor = roleColor;

        var go = new GameObject($"PreviewShard_0_{role}");
        go.transform.SetParent(transform);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = visuals.diamond;
        sr.color = new Color(0.45f, 0.45f, 0.45f, shardMinAlpha);
        sr.sortingOrder = _baseSortingOrder;

        if (!_isArmed && _disarmReason == PhaseStarDisarmReason.NodeResolving)
            sr.enabled = false;

        _previewVisual = go.transform;

        var goB = new GameObject($"PreviewShardB_0_{role}");
        goB.transform.SetParent(transform);
        goB.transform.localPosition = Vector3.zero;
        goB.transform.localRotation = Quaternion.identity;
        goB.transform.localScale = Vector3.one;

        var srB = goB.AddComponent<SpriteRenderer>();
        srB.sprite = visuals.diamond;
        srB.color = new Color(0.45f, 0.45f, 0.45f, shardMinAlpha);
        srB.sortingOrder = _baseSortingOrder;

        if (!_isArmed && _disarmReason == PhaseStarDisarmReason.NodeResolving)
            srB.enabled = false;

        _previewVisualB = goB.transform;

        _buildingPreview = false;

        visuals?.InvalidateShardCache();
        visuals?.BindDualDiamondRenderers(_previewVisual, _previewVisualB);
        visuals?.ResetDualDiamondVisualState();
    }
}
