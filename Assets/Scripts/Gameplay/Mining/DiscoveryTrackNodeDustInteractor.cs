using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class DiscoveryTrackNodeDustInteractor : MonoBehaviour
{
    [SerializeField] private DiscoveryTrackNodeDustInteractorConfig config;

    private static readonly Vector2Int[] kNeighbours8 =
    {
        new(-1,-1), new(0,-1), new(1,-1),
        new(-1, 0),            new(1, 0),
        new(-1, 1), new(0, 1), new(1, 1),
    };

    private Rigidbody2D _rb;
    private GameFlowManager _gfm;
    private DrumTrack   _drumTrack;
    private DiscoveryTrackNode    _node;
    private bool _prevInDust;

    // ---------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------

    /// <summary>Environment feedback scalar consumed by DiscoveryTrackNode locomotion while in dust.</summary>
    public float dustDragScalar => config != null ? config.dustDragScalar : 0f;

    void Awake()
    {
        _rb          = GetComponent<Rigidbody2D>();
        if (_rb == null) TryGetComponent(out _rb);
        _node        = GetComponent<DiscoveryTrackNode>();
        _drumTrack   = (_node != null) ? _node.DrumTrack : null;
        Debug.Assert(config != null,
            $"[DiscoveryTrackNodeDustInteractor] {name} has no config asset assigned — dust interaction will be inert.");
    }

    void FixedUpdate()
    {
        if (_rb == null || _drumTrack == null || config == null) return;

        Vector2    worldPos = _rb.position;
        Vector2Int cell     = _drumTrack.CellOf(worldPos);
        bool       inDust   = _drumTrack.HasDustAt(cell);

        if (inDust && !_prevInDust)
            OnDustEnterGrid(cell);
        _prevInDust = inDust;

        // ---------------------------------------------------------------
        // Dust feel
        // ---------------------------------------------------------------
        if (inDust)
        {
            if (_rb.linearVelocity.sqrMagnitude > 0.0001f)
                _rb.AddForce(-_rb.linearVelocity * config.extraBrake, ForceMode2D.Force);

            // Push back toward the nearest open (non-dust) neighboring cell so the node
            // can't remain embedded in a wall — this is the complement of the edge-hug force.
            Vector2 escapeDir = SumNeighborDirections(cell, requireDust: false);
            if (escapeDir.sqrMagnitude > 0.0001f)
            {
                _rb.AddForce(escapeDir.normalized * config.escapePushForce, ForceMode2D.Force);

                // Hard wall: cancel any velocity component pushing deeper into the dust wall.
                // This prevents driving forces from overpowering the escape push and tunneling through.
                Vector2 wallNormal  = escapeDir.normalized;
                float   intoWallVel = Vector2.Dot(_rb.linearVelocity, wallNormal);
                if (intoWallVel < 0f)
                    _rb.linearVelocity -= wallNormal * intoWallVel;
            }
        }
        else
        {
            RunSkimPaint(cell);
        }
    }

    // ---------------------------------------------------------------
    // Skim paint: kicks up a light exhaust trail on dust cells the node grazes past
    // while cornering along them — scaled by how much it's skimming (perpendicular
    // to the wall) rather than driving straight at it, and by current speed.
    // ---------------------------------------------------------------

    private void RunSkimPaint(Vector2Int cell)
    {
        if (config.exhaustEnergyFraction <= 0f || _node == null) return;

        float speed = _rb.linearVelocity.magnitude;
        if (speed < 0.05f) return;
        Vector2 velN = _rb.linearVelocity / speed;

        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var gen = _gfm?.dustGenerator;
        if (gen == null) return;

        MusicalRole role = _node.GetImprintRole();
        for (int i = 0; i < kNeighbours8.Length; i++)
        {
            var offset = kNeighbours8[i];
            var neighbor = cell + offset;
            if (!_drumTrack.HasDustAt(neighbor)) continue;

            // 1 = skimming past (velocity perpendicular to the wall direction), 0 = driving head-on.
            Vector2 toNeighbor = new Vector2(offset.x, offset.y).normalized;
            float skim = 1f - Mathf.Abs(Vector2.Dot(velN, toNeighbor));
            if (skim < 0.4f) continue;

            float fraction = config.exhaustEnergyFraction * Mathf.Clamp01(speed / 3f) * skim;
            if (fraction < 0.02f) continue;

            gen.PaintDustExhaust(neighbor, role, fraction);
        }
    }

    // ---------------------------------------------------------------
    // Grid-based dust contact
    // ---------------------------------------------------------------

    private void OnDustEnterGrid(Vector2Int cell)
    {
        if (_node == null || _drumTrack == null) return;

        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var gen = _gfm?.dustGenerator;
        if (gen == null) return;
        if (!gen.TryGetCellGo(cell, out var go) || go == null) return;
        if (!go.TryGetComponent<CosmicDust>(out var dust)) return;

        MusicalRole role = _node.GetImprintRole();
        var         prof = _node.RoleProfile;
        if (prof == null) return;

        Color roleColor = prof.GetBaseColor();
        roleColor.a = 1f;
        dust.ApplyRoleAndCharge(role, roleColor, 1f, prof.maxEnergyUnits);
        dust.clearing.drainResistance01 = prof.drainResistance01;

        gen.PaintDustExhaust(cell, role, 1f);
    }

    private Vector2 SumNeighborDirections(Vector2Int center, bool requireDust)
    {
        Vector2 dir = Vector2.zero;
        for (int i = 0; i < kNeighbours8.Length; i++)
        {
            var offset = kNeighbours8[i];
            bool hasDust = _drumTrack.HasDustAt(center + offset);
            if (hasDust == requireDust)
                dir += new Vector2(offset.x, offset.y);
        }
        return dir;
    }

    public void SetLevelAuthority(DrumTrack drumTrack)
    {
        _drumTrack = drumTrack;
    }

    public bool IsInDustAtCurrentCell()
    {
        if (_rb == null || _drumTrack == null) return false;
        return _drumTrack.HasDustAt(_drumTrack.CellOf(_rb.position));
    }
}
