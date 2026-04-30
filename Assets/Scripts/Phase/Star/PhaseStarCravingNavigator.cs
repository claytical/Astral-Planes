using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class PhaseStarCravingNavigator : MonoBehaviour
{
    [Header("Hunter Targeting")]
    [SerializeField] private float replanIntervalSeconds = 0.3f;
    [SerializeField] private bool retargetOnTargetLost = true;

    [Header("Sniffer (per-role diamond facing)")]
    [SerializeField] private float snifferIntervalSeconds = 0.25f;

    [Header("Hunger-Weighted Targeting")]
    [SerializeField, Range(0f, 1f)] private float hungerFloorWeight = 0.15f;

    private PhaseStar _star;
    private bool _active;
    private MusicalRole _attunedRole = MusicalRole.None;
    private bool _huntingEnabled;
    private float _replanTimer;
    private float _snifferTimer;

    private bool _hasTarget;
    private Vector2Int _targetCell;
    private Vector2 _targetDir;

    private bool _hasLockOnCell;
    private Vector2Int _lockOnCell;

    private readonly Dictionary<MusicalRole, Vector2> _snifferDirs = new();
    private Vector2 _nearestColoredDustDir = Vector2.zero;
    private readonly List<Vector2Int> _coloredCellsScratch = new(512);

    public void Initialize(
        PhaseStar star,
        PhaseStarBehaviorProfile profile)
    {
        if (profile != null)
            replanIntervalSeconds = Mathf.Max(0.1f, profile.mazeNavReplanInterval);

        _star = star;
        _active = true;
        ResetTargetingState(clearSniffer: true);
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (!active)
            ResetTargetingState(clearSniffer: false);
    }

    public void SetAttunedRole(MusicalRole role)
    {
        _attunedRole = role;
    }

    public void SetHuntingEnabled(bool enabled)
    {
        _huntingEnabled = enabled;
        if (!enabled)
        {
            _hasTarget = false;
            _hasLockOnCell = false;
            _targetDir = Vector2.zero;
        }
    }

    public bool TryGetTargetForRole(MusicalRole role, out Vector2Int cell)
        => TryGetTargetForRole(role, out cell, null);

    public bool TryGetTargetForRole(
        MusicalRole role,
        out Vector2Int cell,
        HashSet<Vector2Int> excludedCells)
    {
        cell = default;
        if (_attunedRole != MusicalRole.None && role != _attunedRole) return false;

        var gfm = GameFlowManager.Instance;
        var gen = gfm?.dustGenerator;
        var drum = gfm?.activeDrumTrack;
        if (gen == null || drum == null) return false;

        gen.GetColoredDustCells(_coloredCellsScratch);

        Vector2 starWorld = transform.position;
        float bestSqDist = float.MaxValue;
        bool found = false;

        for (int i = 0; i < _coloredCellsScratch.Count; i++)
        {
            var c = _coloredCellsScratch[i];
            if (excludedCells != null && excludedCells.Contains(c)) continue;

            var dust = GetDust(gen, c);
            if (dust == null || dust.Role != role) continue;
            if (!gen.HasDustAt(c)) continue;
            if (dust.currentEnergyUnits <= 0) continue;

            float sqd = ((Vector2)drum.GridToWorldPosition(c) - starWorld).sqrMagnitude;
            if (sqd < bestSqDist)
            {
                bestSqDist = sqd;
                cell = c;
                found = true;
            }
        }

        return found;
    }

    public void NotifyDraining(Vector2Int cell)
    {
        _hasLockOnCell = true;
        _lockOnCell = cell;
        _hasTarget = true;
        _targetCell = cell;
        RefreshTargetDir();
        _replanTimer = replanIntervalSeconds;
    }

    public void ClearLockOn(Vector2Int cell)
    {
        if (_hasLockOnCell && _lockOnCell == cell)
        {
            _hasLockOnCell = false;
            _replanTimer = 0f;
        }
    }

    public Vector2 GetDensitySteerDir() => (_huntingEnabled && _hasTarget) ? _targetDir : Vector2.zero;
    
    public Vector2 GetDominantSnifferDir()
    {
        if (_huntingEnabled && (_hasLockOnCell || _hasTarget))
            return _hasTarget ? _targetDir : Vector2.zero;

        return _nearestColoredDustDir;
    }

    private void Update()
    {
        if (!_active) return;

        float dt = Time.deltaTime;

        RefreshTargetDir();
        ValidateCurrentTarget();

        _replanTimer -= dt;
        if (_replanTimer <= 0f)
        {
            _replanTimer = replanIntervalSeconds;
            HuntTick();
        }

        _snifferTimer -= dt;
        if (_snifferTimer <= 0f)
        {
            _snifferTimer = snifferIntervalSeconds;
            SnifferTick();
        }
    }

    private void HuntTick()
    {
        if (_hasLockOnCell) return;

        var gfm = GameFlowManager.Instance;
        var gen = gfm?.dustGenerator;
        var drum = gfm?.activeDrumTrack;
        if (gen == null || drum == null) return;

        gen.GetColoredDustCells(_coloredCellsScratch);

        if (_coloredCellsScratch.Count == 0)
        {
            _hasTarget = false;
            _targetDir = Vector2.zero;
            return;
        }

        Vector2 starWorld = transform.position;
        Vector2Int bestCell = default;
        float bestEffDist = float.MaxValue;
        bool found = false;

        for (int i = 0; i < _coloredCellsScratch.Count; i++)
        {
            var cell = _coloredCellsScratch[i];
            var dust = GetDust(gen, cell);
            if (dust == null || dust.Role == MusicalRole.None) continue;
            if (dust.currentEnergyUnits <= 0) continue;

            float hunger = _star != null ? _star.GetRoleHunger(dust.Role) : 1f;
            float score = Mathf.Lerp(hungerFloorWeight, 1f, hunger);

            Vector2 cellWorld = drum.GridToWorldPosition(cell);
            float dist = (cellWorld - starWorld).magnitude;
            float effDist = dist / Mathf.Max(0.001f, score);

            if (effDist < bestEffDist)
            {
                bestCell = cell;
                bestEffDist = effDist;
                found = true;
            }
        }

        if (!found)
        {
            _hasTarget = false;
            _targetDir = Vector2.zero;
            return;
        }

        _hasTarget = true;
        _targetCell = bestCell;
        RefreshTargetDir();
    }

    private void SnifferTick()
    {
        var gfm = GameFlowManager.Instance;
        var gen = gfm?.dustGenerator;
        var drum = gfm?.activeDrumTrack;
        if (gen == null || drum == null) return;

        gen.GetColoredDustCells(_coloredCellsScratch);

        _snifferDirs.Clear();

        Vector2 starWorld = transform.position;
        bool foundAny = false;
        float nearestSqDist = float.MaxValue;
        Vector2Int nearestCell = default;

        var nearestSqDistPerRole = new Dictionary<MusicalRole, float>(4);

        for (int i = 0; i < _coloredCellsScratch.Count; i++)
        {
            var cell = _coloredCellsScratch[i];
            var dust = GetDust(gen, cell);
            if (dust == null || dust.Role == MusicalRole.None) continue;
            if (dust.currentEnergyUnits <= 0) continue;

            Vector2 cellWorld = drum.GridToWorldPosition(cell);
            float sqDist = (cellWorld - starWorld).sqrMagnitude;

            if (!nearestSqDistPerRole.TryGetValue(dust.Role, out float best) || sqDist < best)
            {
                nearestSqDistPerRole[dust.Role] = sqDist;
                Vector2 dir = cellWorld - starWorld;
                if (dir.sqrMagnitude > 0.0001f)
                    _snifferDirs[dust.Role] = dir.normalized;
            }

            if (sqDist < nearestSqDist)
            {
                nearestSqDist = sqDist;
                nearestCell = cell;
                foundAny = true;
            }
        }

        if (foundAny)
        {
            Vector2 cellWorld = drum.GridToWorldPosition(nearestCell);
            Vector2 dir = cellWorld - starWorld;
            _nearestColoredDustDir = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector2.zero;
        }
        else
        {
            _nearestColoredDustDir = Vector2.zero;
        }
    }

    private void RefreshTargetDir()
    {
        if (!_hasTarget)
        {
            _targetDir = Vector2.zero;
            return;
        }

        var drum = GameFlowManager.Instance?.activeDrumTrack;
        if (drum == null)
        {
            _targetDir = Vector2.zero;
            return;
        }

        Vector2 cellWorld = drum.GridToWorldPosition(_targetCell);
        Vector2 dir = cellWorld - (Vector2)transform.position;
        _targetDir = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector2.zero;
    }

    private void ValidateCurrentTarget()
    {
        if (_hasLockOnCell && !IsTargetValid(_lockOnCell))
        {
            _hasLockOnCell = false;
            _hasTarget = false;
            _targetDir = Vector2.zero;
            _replanTimer = 0f;
            return;
        }

        if (_hasTarget && retargetOnTargetLost && !IsTargetValid(_targetCell))
        {
            _hasTarget = false;
            _targetDir = Vector2.zero;
            _replanTimer = 0f;
        }
    }

    private void ResetTargetingState(bool clearSniffer)
    {
        _replanTimer = 0f;
        _snifferTimer = 0f;
        _hasTarget = false;
        _targetDir = Vector2.zero;
        _hasLockOnCell = false;
        _huntingEnabled = false;

        if (clearSniffer)
        {
            _snifferDirs.Clear();
            _nearestColoredDustDir = Vector2.zero;
        }
    }

    private bool IsTargetValid(Vector2Int cell)
    {
        var gen = GameFlowManager.Instance?.dustGenerator;
        if (gen == null) return false;

        var dust = GetDust(gen, cell);
        return dust != null &&
               dust.Role != MusicalRole.None &&
               dust.currentEnergyUnits > 0 &&
               gen.HasDustAt(cell);
    }

    private static CosmicDust GetDust(CosmicDustGenerator gen, Vector2Int gp)
    {
        if (!gen.TryGetCellGo(gp, out var go) || go == null) return null;
        go.TryGetComponent<CosmicDust>(out var dust);
        return dust;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (_hasTarget)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(transform.position, _targetDir * 2f);

            var drum = GameFlowManager.Instance?.activeDrumTrack;
            if (drum != null)
            {
                Vector2 tw = drum.GridToWorldPosition(_targetCell);
                Gizmos.color = Color.red;
                Gizmos.DrawWireSphere(tw, 0.25f);
            }
        }

        foreach (var kvp in _snifferDirs)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawRay(transform.position, kvp.Value * 0.8f);
        }
    }
#endif
}
