using System.Collections.Generic;
using UnityEngine;

// SuperNode tell (LEGIBILITY_AUDIT.md B4): when this star's next ejection will be a SuperNode
// instead of a normal DiscoveryTrackNode — its track is fully expanded and the motif has an
// alternate chord progression (ShouldSpawnSuperNodeForTrack) — a slowly turning ring striped
// with every active role's color surrounds the star. The SuperNode is a multi-track bonus
// round, so the ring wears all the tracks it will touch. Hidden whenever the star itself is
// hidden; faded while the star is asleep (PhaseStar.PokeFeedback.cs).
public partial class PhaseStar
{
    private const int   kSuperRingSegments    = 72;
    private const float kSuperRingRadiusMul   = 1.6f;   // × star collider radius
    private const float kSuperRingMinRadius   = 0.55f;
    private const float kSuperRingWidth       = 0.06f;
    private const float kSuperRingSpinDeg     = 40f;
    private const float kSuperRingAlpha       = 0.9f;
    private const float kSuperRingSleepAlpha  = 0.3f;
    private const float kSuperRingBreathHz    = 0.6f;
    private const float kSuperRingFadeSpeed   = 5f;
    private const float kSuperRingCheckPeriod = 0.25f;

    private LineRenderer _superRing;
    private float _superRingAlpha;
    private bool _superNodeNext;
    private float _nextSuperNodeCheck;
    private int _superRingGradientRoleCount = -1;

    // Same rule as ShouldSpawnSuperNodeForTrack, without its verbose logging (polled).
    private bool IsSuperNodeNextEjection()
    {
        if (_attunedRole == MusicalRole.None) return false;
        if (_assignedMotif == null || _assignedMotif.alternateChordProgressionProfile == null) return false;
        var track = _cachedTrack != null ? _cachedTrack : FindTrackByRole(_attunedRole);
        return track != null && track.loopMultiplier >= Mathf.Max(1, track.maxLoopMultiplier);
    }

    private bool IsStarVisibleForTell()
    {
        if (_burstOffScreen || _isDisposing || _previewVisual == null) return false;
        var sr = _previewVisual.GetComponent<SpriteRenderer>();
        return sr != null && sr.enabled && sr.gameObject.activeInHierarchy;
    }

    private void TickSuperNodeTell(float dt)
    {
        if (Time.unscaledTime >= _nextSuperNodeCheck)
        {
            _nextSuperNodeCheck = Time.unscaledTime + kSuperRingCheckPeriod;
            _superNodeNext = IsSuperNodeNextEjection();
            if (_superNodeNext && _superRing != null) RefreshSuperRingGradient();
        }

        bool show = _superNodeNext && IsStarVisibleForTell();
        float target = show ? (_sleepVisual ? kSuperRingSleepAlpha : kSuperRingAlpha) : 0f;
        _superRingAlpha = Mathf.Lerp(_superRingAlpha, target, 1f - Mathf.Exp(-kSuperRingFadeSpeed * dt));

        if (_superRingAlpha < 0.005f)
        {
            if (_superRing != null && _superRing.enabled) _superRing.enabled = false;
            return;
        }

        EnsureSuperRing();
        _superRing.enabled = true;

        // Gradient carries the role stripes at full alpha; fade/breath ride the material tint
        // (Sprites/Default multiplies vertex color by _Color), so nothing allocates per frame.
        float breath = 0.85f + 0.15f * Mathf.Sin(Time.time * kSuperRingBreathHz * Mathf.PI * 2f);
        _superRing.material.color = new Color(1f, 1f, 1f, _superRingAlpha * breath);
        _superRing.transform.Rotate(0f, 0f, -kSuperRingSpinDeg * dt);

        // Constant world size regardless of the star root's scale.
        Vector3 ls = transform.lossyScale;
        _superRing.transform.localScale = new Vector3(
            Mathf.Abs(ls.x) > 0.0001f ? 1f / ls.x : 1f,
            Mathf.Abs(ls.y) > 0.0001f ? 1f / ls.y : 1f, 1f);
    }

    private void EnsureSuperRing()
    {
        if (_superRing != null) return;

        float radius = kSuperRingMinRadius;
        var col = GetComponent<Collider2D>();
        if (col != null) radius = Mathf.Max(radius, Mathf.Max(col.bounds.extents.x, col.bounds.extents.y) * kSuperRingRadiusMul);

        var go = new GameObject("SuperNode Ring");
        go.transform.SetParent(transform, false);
        _superRing = go.AddComponent<LineRenderer>();
        _superRing.useWorldSpace = false;
        _superRing.loop = true;
        _superRing.positionCount = kSuperRingSegments;
        _superRing.widthMultiplier = kSuperRingWidth;
        _superRing.material = new Material(Shader.Find("Sprites/Default"));
        _superRing.sortingOrder = Mathf.Max(0, _baseSortingOrder - 1); // behind the diamonds
        for (int i = 0; i < kSuperRingSegments; i++)
        {
            float a = i / (float)kSuperRingSegments * Mathf.PI * 2f;
            _superRing.SetPosition(i, new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * radius);
        }
        _superRingGradientRoleCount = -1;
        RefreshSuperRingGradient();
    }

    private readonly List<Color> _superRingColors = new();

    // Stripes of every active role's color around the ring (wrapping back to the first),
    // so the bonus round reads as "all tracks". Rebuilt only when the role set changes
    // (checked on the 4 Hz poll, not per frame).
    private void RefreshSuperRingGradient()
    {
        var roles = _assignedMotif != null ? _assignedMotif.GetActiveRoles() : null;
        int n = roles != null ? roles.Count : 0;
        if (n != _superRingGradientRoleCount)
        {
            _superRingColors.Clear();
            if (roles != null)
                foreach (var r in roles)
                    _superRingColors.Add(ResolveRoleColor(r, FindTrackByRole(r)));
            if (_superRingColors.Count == 0) _superRingColors.Add(_previewColor);
            _superRingGradientRoleCount = n;
        }
        else return;

        int count = Mathf.Min(_superRingColors.Count, 7); // Gradient allows 8 keys incl. wrap
        var ck = new GradientColorKey[count + 1];
        for (int i = 0; i < count; i++) ck[i] = new GradientColorKey(_superRingColors[i], i / (float)count);
        ck[count] = new GradientColorKey(_superRingColors[0], 1f);
        var ak = new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) };
        var g = new Gradient();
        g.SetKeys(ck, ak);
        _superRing.colorGradient = g;
    }
}
