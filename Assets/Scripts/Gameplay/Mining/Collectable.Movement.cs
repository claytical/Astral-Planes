using System.Collections;
using UnityEngine;

public partial class Collectable
{
    private static readonly Vector2Int[] kDirs8 =
    {
        new Vector2Int( 1, 0),
        new Vector2Int(-1, 0),
        new Vector2Int( 0, 1),
        new Vector2Int( 0,-1),
        new Vector2Int( 1, 1),
        new Vector2Int( 1,-1),
        new Vector2Int(-1, 1),
        new Vector2Int(-1,-1),
    };

    private void TryBindLoopBoundary()
    {
        if (!useLoopBoundaryIdea) return;
        if (assignedInstrumentTrack == null) return;

        var dt = assignedInstrumentTrack.drumTrack;
        if (dt == null) return;

        if (_boundDrumTrack == dt) return;

        if (_boundDrumTrack != null)
            _boundDrumTrack.OnLoopBoundary -= HandleLoopBoundaryIdea;

        _boundDrumTrack = dt;
        _boundDrumTrack.OnLoopBoundary += HandleLoopBoundaryIdea;
    }

    private void UnbindLoopBoundary()
    {
        if (_boundDrumTrack != null)
            _boundDrumTrack.OnLoopBoundary -= HandleLoopBoundaryIdea;

        _boundDrumTrack = null;
    }

    private void HandleLoopBoundaryIdea()
    {
        if (!useLoopBoundaryIdea) return;
        if (_inCarry) return;

        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var dustGen = (_gfm != null) ? _gfm.dustGenerator : null;
        var dt = (assignedInstrumentTrack != null) ? assignedInstrumentTrack.drumTrack : null;
        if (dustGen == null || dt == null) return;

        Vector2 cur = (_rb != null) ? _rb.position : (Vector2)transform.position;

        _ideaDir = ChooseIdeaDirection(cur, dt, dustGen, Mathf.Max(1, ideaLookaheadCells));
        if (_ideaDir.sqrMagnitude < 0.0001f)
            _ideaDir = UnityEngine.Random.insideUnitCircle.normalized;
    }

    private Vector2 ChooseIdeaDirection(Vector2 worldPos, DrumTrack dt, CosmicDustGenerator dustGen, int lookaheadCells)
    {
        int w = dt.GetSpawnGridWidth();
        int h = dt.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return Vector2.zero;

        Vector2Int c = dt.WorldToGridPosition(worldPos);
        c.x = Mathf.Clamp(c.x, 0, w - 1);
        c.y = Mathf.Clamp(c.y, 0, h - 1);

        float bestScore = float.NegativeInfinity;
        Vector2Int best = Vector2Int.zero;

        for (int d = 0; d < kDirs8.Length; d++)
        {
            var dir = kDirs8[d];
            float score = 0f;

            for (int i = 1; i <= lookaheadCells; i++)
            {
                var gp = c + dir * i;
                if (gp.x < 0 || gp.y < 0 || gp.x >= w || gp.y >= h)
                    break;

                bool hasDust = dustGen.HasDustAt(gp);
                // Reward open space; penalize dust.
                score += hasDust ? -2.0f : +1.0f;

                // Immediate wall is especially bad (jail ring effect).
                if (i == 1 && hasDust)
                    score -= 2.0f;
            }

            // Lead: bonus for directions that have dust walls on either flank — hugs maze edges.
            if (_role == MusicalRole.Lead)
            {
                var probe1 = c + dir;
                var perp1 = new Vector2Int(-dir.y, dir.x);
                var perp2 = new Vector2Int(dir.y, -dir.x);
                if (dustGen.HasDustAt(probe1 + perp1)) score += 1.5f;
                if (dustGen.HasDustAt(probe1 + perp2)) score += 1.5f;
            }

            // Harmony: prefer directions that continue the orbital arc (chirality-locked turn bias).
            if (_role == MusicalRole.Harmony && _ideaDirSmoothed.sqrMagnitude > 0.01f)
            {
                Vector2 perpCCW = new Vector2(-_ideaDirSmoothed.y, _ideaDirSmoothed.x).normalized;
                Vector2 wdir2 = new Vector2(dir.x, dir.y).normalized;
                float alignment = Vector2.Dot(wdir2, perpCCW) * _orbitalChirality;
                float orbBias = _roleProfile != null ? _roleProfile.orbitalTurnBias : 0.6f;
                score += alignment * orbBias;
            }

            // Tiny randomness breaks ties and makes behavior feel "alive".
            score += UnityEngine.Random.Range(-0.15f, +0.15f);

            if (score > bestScore)
            {
                bestScore = score;
                best = dir;
            }
        }

        Vector2 wdir = new Vector2(best.x, best.y);
        if (wdir.sqrMagnitude > 0f) wdir.Normalize();
        return wdir;
    }

    private bool IsInsideDustStable(Vector2 worldPos, DrumTrack dt, CosmicDustGenerator dustGen)
    {
        if (dt == null || dustGen == null) return IsPositionInsideDust(worldPos);
        Vector2Int gp = dt.WorldToGridPosition(worldPos);
        return dustGen.HasDustAt(gp);
    }

    private bool IsPositionInsideDust(Vector2 worldPos)
    {
        int count = Physics2D.OverlapCircleNonAlloc(worldPos, dustAdjacencyProbe, _dustProbeHits);
        for (int i = 0; i < count; i++)
        {
            var dust = _dustProbeHits[i] ? _dustProbeHits[i].GetComponent<CosmicDust>() : null;
            if (!dust) continue;
            Vector2 cp = _dustProbeHits[i].ClosestPoint(worldPos);
            if ((cp - worldPos).sqrMagnitude < 1e-6f) return true;
        }
        return false;
    }

    private float Duration01()
    {
        float t = Mathf.Clamp(noteDurationTicks, 1, 16);
        return (t - 1f) / 15f;
    }

    private float ComputeMoveSpeed()
    {
        float d = Duration01();
        float base_ = Mathf.Lerp(maxSpeed, minSpeed, d);
        float roleMul = _roleProfile != null ? _roleProfile.collectableDriftSpeedMultiplier : 1f;
        return base_ * roleMul;
    }

    private int StableSeed()
    {
        unchecked
        {
            int a = assignedInstrumentTrack ? assignedInstrumentTrack.GetHashCode() : 17;
            int b = assignedNote;
            int c = noteDurationTicks;
            return (a * 486187739) ^ (b * 1009) ^ (c * 9176);
        }
    }

    private IEnumerator MovementRoutine()
    {
        if (!_rb && !TryGetComponent(out _rb)) yield break;

        _speed = ComputeMoveSpeed();
        const float TRAPPED_DRIFT_MUL = 0.35f;

        _rng ??= new System.Random(StableSeed());
        float seedA = (float)_rng.NextDouble() * 1000f;
        float seedB = (float)_rng.NextDouble() * 1000f;

        while (true)
        {
            yield return new WaitForFixedUpdate();
            if (_rb == null) continue;
            if (_inCarry) continue;
            if (_gfm == null) _gfm = GameFlowManager.Instance;
            var dustGen = (_gfm != null) ? _gfm.dustGenerator : null;
            var dt = (assignedInstrumentTrack != null) ? assignedInstrumentTrack.drumTrack : null;

            Vector2 cur = _rb.position;

            bool insideDust = false;
            if (dustGen != null && dt != null)
                insideDust = IsInsideDustStable(cur, dt, dustGen);
            else
                insideDust = IsPositionInsideDust(cur);
            if (isTrappedInDust && insideDust)
                continue;

            Vector2 step = Vector2.zero;

            // Lead: re-evaluate idea direction more frequently for darting wall-racing.
            if (_role == MusicalRole.Lead && dt != null && dustGen != null)
            {
                _leadRefreshTimer -= Time.fixedDeltaTime;
                if (_leadRefreshTimer <= 0f)
                {
                    _leadRefreshTimer = dt.GetLoopLengthInSeconds() * 0.25f;
                    HandleLoopBoundaryIdea();
                }
            }

            // Open-space relocation: periodically re-evaluate idea and sprint toward it.
            if (!insideDust && _roleProfile != null && _roleProfile.collectableOpenSpaceRelocateInterval > 0f)
            {
                _openSpaceRelocateTimer -= Time.fixedDeltaTime;
                if (_openSpaceRelocateTimer <= 0f)
                {
                    _openSpaceRelocateTimer = _roleProfile.collectableOpenSpaceRelocateInterval;
                    HandleLoopBoundaryIdea();
                    if (_roleProfile.collectableOpenSpaceSprintDuration > 0f)
                    {
                        _isSprinting = true;
                        _sprintTimer = _roleProfile.collectableOpenSpaceSprintDuration;
                    }
                }
            }

            if (_isSprinting)
            {
                _sprintTimer -= Time.fixedDeltaTime;
                if (_sprintTimer <= 0f) _isSprinting = false;
            }

            float effectiveSpeed = _speed;
            if (_isSprinting && _roleProfile != null)
                effectiveSpeed *= _roleProfile.collectableOpenSpaceSprintMultiplier;

            if (_isSprinting)
                _ideaDirSmoothed = _ideaDir;
            else
                _ideaDirSmoothed = Vector2.Lerp(_ideaDirSmoothed, _ideaDir, Mathf.Clamp01(Time.fixedDeltaTime * ideaTurnLerp));

            if (_ideaDirSmoothed.sqrMagnitude > 0.0001f)
                step += _ideaDirSmoothed.normalized * (effectiveSpeed * ideaBiasStrength * Time.fixedDeltaTime);

            if (!_isSprinting)
            {
                float nt = Time.time;
                float nx = Mathf.PerlinNoise(seedA, nt * 0.35f) * 2f - 1f;
                float ny = Mathf.PerlinNoise(seedB, nt * 0.35f) * 2f - 1f;
                Vector2 turb = new Vector2(nx, ny);
                if (turb.sqrMagnitude > 0.0001f)
                    step += turb.normalized * (microTurbulenceStrength * effectiveSpeed * Time.fixedDeltaTime);
            }

            // Bass: BPM-synced vertical bob.
            if (_role == MusicalRole.Bass && dt != null)
            {
                float beatsPerSec = dt.drumLoopBPM / 60f;
                float bob = Mathf.Sin((float)(AudioSettings.dspTime * beatsPerSec * Mathf.PI * 2.0));
                step.y += bob * 0.012f;
            }

            // Groove: beat-snapped burst/pause.
            if (_role == MusicalRole.Groove && dt != null)
            {
                float burst = _roleProfile != null ? _roleProfile.burstDuration : 0.4f;
                float pause = _roleProfile != null ? _roleProfile.pauseDuration : 0.35f;
                _groovePhaseTimer -= Time.fixedDeltaTime;
                if (_groovePhaseTimer <= 0f)
                {
                    _grooveBurstActive = !_grooveBurstActive;
                    _groovePhaseTimer = _grooveBurstActive ? burst : pause;
                }
                if (!_grooveBurstActive)
                    step = Vector2.zero;
            }

            if (insideDust)
                step *= TRAPPED_DRIFT_MUL;

            float maxStep = effectiveSpeed * Time.fixedDeltaTime;
            if (step.sqrMagnitude > maxStep * maxStep)
                step = step.normalized * maxStep;

            Vector2 nextPos = cur + step;
            ClampToViewport(ref nextPos);
            _rb.MovePosition(nextPos);
        }
    }

    private void ClampToViewport(ref Vector2 pos)
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return;
        const float pad = 0.4f;
        Vector2 mn = _cam.ViewportToWorldPoint(Vector3.zero);
        Vector2 mx = _cam.ViewportToWorldPoint(Vector3.one);
        pos.x = Mathf.Clamp(pos.x, mn.x + pad, mx.x - pad);
        pos.y = Mathf.Clamp(pos.y, mn.y + pad, mx.y - pad);
    }

    public static bool IsCellFreeStatic(Vector2Int cell)
    {
        lock (_lock)
        {
            if (_occupantByCell.TryGetValue(cell, out var occ) && occ != null) return false;
            if (_reservedByCell.TryGetValue(cell, out var res) && res != null) return false;
            return true;
        }
    }

    private void RegisterOccupant(Vector2Int cell)
    {
        lock (_lock)
        {
            _occupantByCell[cell] = this;
        }
        _currentCell = cell;
    }

    private void UnregisterOccupant()
    {
        lock (_lock)
        {
            if (_occupantByCell.TryGetValue(_currentCell, out var c) && c == this)
                _occupantByCell.Remove(_currentCell);
        }
    }

    private void ClearReservation()
    {
        if (!_hasReservation) return;

        lock (_lock)
        {
            if (_reservedByCell.TryGetValue(_reservedCell, out var c) && c == this)
                _reservedByCell.Remove(_reservedCell);
        }

        _hasReservation = false;
    }

    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (_rb == null && !TryGetComponent(out _rb)) return;

        if (coll.collider != null && coll.collider.GetComponent<CosmicDust>() != null)
        {
            Vector2 away = Vector2.zero;

            if (coll.contactCount > 0)
                away = coll.GetContact(0).normal;
            if (away.sqrMagnitude < 0.0001f)
                away = (_rb.position - (Vector2)coll.collider.bounds.center);
            if (away.sqrMagnitude < 0.0001f)
                away = UnityEngine.Random.insideUnitCircle;

            away.Normalize();
            _rb.AddForce(away * dustCollisionEnterImpulse, ForceMode2D.Impulse);
        }
    }

    private void OnCollisionStay2D(Collision2D coll)
    {
        if (_rb == null) return;
        if (coll.collider == null) return;
        if (coll.collider.GetComponent<CosmicDust>() == null) return;

        Vector2 n = Vector2.zero;
        if (coll.contactCount > 0) n = coll.GetContact(0).normal;
        if (n.sqrMagnitude < 0.0001f) n = (_rb.position - (Vector2)coll.collider.bounds.center);

        if (n.sqrMagnitude > 0.0001f)
        {
            n.Normalize();
            float into = Vector2.Dot(_rb.linearVelocity, -n);
            if (into > 0f) _rb.linearVelocity += n * into;
            _rb.AddForce(n * dustCollisionStayForce, ForceMode2D.Force);
        }
    }
}
