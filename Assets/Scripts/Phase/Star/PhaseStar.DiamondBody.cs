using System.Collections.Generic;
using UnityEngine;

// The star's countable body. A star is a tangle of tentacles, one per note it still needs
// (PhaseStarDustAffect.Buds.cs); every second note it catches forms an isometric diamond, and the
// final note always forms one, so a finished N-note star is ceil(N/2) diamonds crossed through
// the centre: 1 diamond = 2 points, 2 = a 4-point star, 3 = 6 points...
// "5 tentacles: 5 notes to go. 3 tentacles and a diamond: 3 to go."
public partial class PhaseStar
{
    private const int   kMaxDiamonds        = 8;
    private const float kDiamondGrowSeconds = 0.35f;
    private const float kDiamondPop         = 0.25f; // overshoot while forming

    private sealed class StarDiamond
    {
        public SpriteRenderer sr;
        public float grow01;
        public float angle;
        public float angleVel;
    }

    private readonly List<StarDiamond> _diamonds = new List<StarDiamond>();
    private bool _diamondAnglesInit;

    /// <summary>Diamonds a fully charged star shows for its current note count (0 when blocked).</summary>
    private int DiamondPoolSize()
    {
        if (requiredZapCount <= 0 || requiredZapCount == int.MaxValue) return 0;
        return Mathf.Min(kMaxDiamonds, (RequiredZapCount + 1) / 2);
    }

    /// <summary>Diamonds formed so far: one per two notes caught, plus the last one when complete.</summary>
    private int DiamondsFormed()
    {
        int pool = DiamondPoolSize();
        if (pool == 0) return 0;
        int caught = Mathf.Max(0, zappedCount);
        if (caught >= RequiredZapCount) return pool;
        return Mathf.Min(pool, caught / 2);
    }

    private void EnsureDiamondCount(int n)
    {
        n = Mathf.Clamp(n, 0, kMaxDiamonds);
        if (_diamonds.Count == n) return;

        while (_diamonds.Count > n)
        {
            var d = _diamonds[_diamonds.Count - 1];
            _diamonds.RemoveAt(_diamonds.Count - 1);
            if (d.sr != null)
            {
                visuals?.UnregisterStarDiamond(d.sr);
                d.sr.transform.SetParent(null);
                Destroy(d.sr.gameObject);
            }
        }

        while (_diamonds.Count < n && visuals != null)
        {
            var go = new GameObject($"StarDiamond_{_diamonds.Count}");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.zero; // unformed

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = visuals.diamond;
            sr.color = _previewColor;
            sr.sortingOrder = BaseSortingOrder;
            if (!_isArmed && _disarmReason == PhaseStarDisarmReason.NodeResolving) sr.enabled = false;
            // Match the body's current visibility so a hidden star doesn't flash a new diamond.
            if (_diamonds.Count > 0 && _diamonds[0].sr != null) sr.enabled = _diamonds[0].sr.enabled;

            visuals.RegisterStarDiamond(sr);
            _diamonds.Add(new StarDiamond { sr = sr, angle = _diamonds.Count > 0 ? _diamonds[0].angle : 0f });
        }

        _previewVisual = _diamonds.Count > 0 ? _diamonds[0].sr.transform : null;
        visuals?.InvalidateShardCache();
    }

    private void DestroyAllDiamonds()
    {
        EnsureDiamondCount(0);
        _diamondAnglesInit = false;
    }

    // Per frame: diamonds form/unform with the count, re-space evenly through the centre, and spin.
    private void UpdateStarBody(float dt, float baseAngle, bool ready)
    {
        if (visuals == null) return;
        EnsureDiamondCount(DiamondPoolSize());
        if (_diamonds.Count == 0) return;

        int formed = DiamondsFormed();
        float spin = ready ? baseAngle * ReadyRotSpeedMul : baseAngle;
        float shake = visuals.CurrentShakeDeg();
        float smooth = visuals.DiamondAngleSmoothTime;

        Color tint = _previewRole != MusicalRole.None ? _previewColor : Color.gray;
        tint.a = 1f;
        tint = visuals.ApplySleepTint(tint);
        Sprite sprite = visuals.FormedDiamondSprite(_previewRole);

        for (int i = 0; i < _diamonds.Count; i++)
        {
            var d = _diamonds[i];
            if (d.sr == null) continue;

            d.grow01 = Mathf.MoveTowards(d.grow01, i < formed ? 1f : 0f, dt / kDiamondGrowSeconds);
            float g = Mathf.SmoothStep(0f, 1f, d.grow01);
            float scale = g * (1f + kDiamondPop * Mathf.Sin(Mathf.PI * g));

            float target = spin + i * 180f / Mathf.Max(1, formed);
            if (!_diamondAnglesInit) { d.angle = target; d.angleVel = 0f; }
            else d.angle = Mathf.SmoothDampAngle(d.angle, target, ref d.angleVel, smooth);

            d.sr.sprite = sprite;
            d.sr.color = tint;
            var tr = d.sr.transform;
            tr.localRotation = Quaternion.Euler(0f, 0f, d.angle + shake);
            tr.localScale = Vector3.one * scale;
        }
        _diamondAnglesInit = true;
    }
}
