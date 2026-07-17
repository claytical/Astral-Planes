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

    /// <summary>
    /// Intent-driven velocity steering. The rigidbody stays Dynamic so dust collisions
    /// genuinely deflect it; steering pulls back toward the role pattern's desired velocity.
    /// The directional intent is held for _intentInterval (the note's duration in musical
    /// time) before being re-picked.
    /// </summary>
    private IEnumerator MovementRoutine()
    {
        if (!_rb && !TryGetComponent(out _rb)) yield break;
        _rng ??= new System.Random(StableSeed());

        while (true)
        {
            yield return new WaitForFixedUpdate();
            if (_rb == null || !_rb.simulated) continue;
            if (_inCarry) continue;
            // Spawn arrival (Kinematic) and deposit/carry paths own the body.
            if (_rb.bodyType != RigidbodyType2D.Dynamic) continue;

            if (_gfm == null) _gfm = GameFlowManager.Instance;
            var dustGen = (_gfm != null) ? _gfm.dustGenerator : null;
            var drums = (assignedInstrumentTrack != null) ? assignedInstrumentTrack.drumTrack : null;
            float fdt = Time.fixedDeltaTime;

            Vector2 cur = _rb.position;

            // Only a collectable with dust on its own cell AND all 8 neighbors is trapped;
            // anything less keeps moving (and bouncing) so it never reads as stuck.
            if (IsFullyTrapped(cur, drums, dustGen))
            {
                _rb.linearVelocity = Vector2.MoveTowards(_rb.linearVelocity, Vector2.zero, SteerAccel() * fdt);
                continue;
            }

            // The intent window is armed by HandleTimelineStep when the playhead crosses
            // this note's step; between pulses the note rests near home.
            _intentTimer = Mathf.Max(0f, _intentTimer - fdt);

            Vector2 desired;
            if (TryGetFleeThreat(cur, out Vector2 threatPos))
            {
                // Flee overrides the pattern AND the home tether until the vehicle
                // is a safe distance away; then the tether reels the note back.
                Vector2 awayDir = cur - threatPos;
                awayDir = awayDir.sqrMagnitude > 0.0001f
                    ? awayDir.normalized
                    : UnityEngine.Random.insideUnitCircle.normalized;
                desired = awayDir * (_speed * FleeSpeedMul());
            }
            else
            {
                // Harmony never fully rests — it keeps a slow anchored orbit between pulses.
                bool resting = _intentTimer <= 0f;
                desired = (resting && _role != MusicalRole.Harmony)
                    ? Vector2.zero
                    : ComputeDesiredVelocity(cur, fdt, resting);

                // Elastic home tether: no pull inside the free radius; beyond it the inward
                // pull grows with distance until it overpowers the pattern speed.
                // Harmony's ring spring already anchors it home; stacking the tether on top
                // would distort the circle.
                if (_role != MusicalRole.Harmony)
                {
                    Vector2 toHome = _homeWorld - cur;
                    float homeDist = toHome.magnitude;
                    float excess = homeDist - _homeFreeRadius;
                    if (excess > 0f && homeDist > 0.001f)
                        desired += (toHome / homeDist) * (excess * HomePullPerUnit());
                }
            }

            // Right after a dust hit, steer weakly so the bounce visibly plays out
            // before the intent reasserts itself. Accel scales with the desired speed so
            // duration-derived slams/surges still reach full speed in _accelSeconds.
            _bounceRecoverTimer = Mathf.Max(0f, _bounceRecoverTimer - fdt);
            float steer = Mathf.Max(SteerAccel(), desired.magnitude / _accelSeconds)
                          * (_bounceRecoverTimer > 0f ? 0.25f : 1f);

            _rb.linearVelocity = Vector2.MoveTowards(_rb.linearVelocity, desired, steer * fdt);

            ClampToViewportRb();
        }
    }

    private float SteerAccel() => _steerAccel;

    /// <summary>
    /// Timeline ghost pulse: fired by DrumTrack.OnStepChanged (absolute step on the
    /// expanded leader loop). When the playhead crosses this note's step, the drifting
    /// collectable sounds its note softly (half authored velocity) and dances in a
    /// fresh direction for the note's duration.
    /// </summary>
    private void HandleTimelineStep(int step, int leaderSteps)
    {
        if (_handled || _inCarry) return;
        if (step != intendedStep) return;

        if (assignedInstrumentTrack != null)
            assignedInstrumentTrack.PlayOneShotMidi(assignedNote, spawnVelocity127 * 0.5f, noteDurationTicks);

        PickIntent();
        _intentTimer = _intentInterval;
    }

    private void UnbindTimelineStep()
    {
        if (_boundStepDrums != null)
        {
            _boundStepDrums.OnStepChanged -= HandleTimelineStep;
            _boundStepDrums = null;
        }
    }

    private float HomePullPerUnit() => _roleProfile != null ? _roleProfile.collectableHomePullPerUnit : 1.5f;

    private float FleeSpeedMul() => _roleProfile != null ? _roleProfile.collectableFleeSpeedMul : 1.3f;

    private bool TryGetFleeThreat(Vector2 cur, out Vector2 threatPos)
    {
        threatPos = default;
        if (_fleeRadiusWorld <= 0f) { _isFleeing = false; return false; }
        var vehicles = _gfm != null ? _gfm.GetVehicles() : null;
        if (vehicles == null || vehicles.Count == 0) { _isFleeing = false; return false; }

        // Hysteresis: once fleeing, stay fleeing until the vehicle is 1.5× the trigger away.
        float trigger = _isFleeing ? _fleeRadiusWorld * 1.5f : _fleeRadiusWorld;
        float bestSq = trigger * trigger;
        bool found = false;
        for (int i = 0; i < vehicles.Count; i++)
        {
            var v = vehicles[i];
            if (v == null) continue;
            float dsq = ((Vector2)v.transform.position - cur).sqrMagnitude;
            if (dsq < bestSq)
            {
                bestSq = dsq;
                threatPos = v.transform.position;
                found = true;
            }
        }
        _isFleeing = found;
        return found;
    }

    private void PickIntent()
    {
        _rng ??= new System.Random(StableSeed());
        switch (_role)
        {
            case MusicalRole.Bass:
            {
                // Piston: each charge spans from the current position to the opposite
                // cage edge in exactly one note duration at multiplier 1 — short notes
                // slam, long notes press. The cage (home radius) sets the reach;
                // collectableSpeed scales the whole charge.
                _bassChargeSign = -_bassChargeSign;
                _intentDir = new Vector2(0f, _bassChargeSign);
                float curY = _rb != null ? _rb.position.y : transform.position.y;
                float targetY = _homeWorld.y + _bassChargeSign * _homeFreeRadius;
                _bassChargeSpeed = Mathf.Max(_speed, _profileSpeedMul * Mathf.Abs(targetY - curY) / _intentInterval);
                break;
            }

            case MusicalRole.Groove:
                _intentDir = (_rng.Next(2) == 0) ? Vector2.left : Vector2.right;
                _grooveBurstActive = true;
                _groovePhaseTimer = _roleProfile != null ? _roleProfile.burstDuration : 0.4f;
                break;

            case MusicalRole.Harmony:
                // Nothing to re-pick: the orbit angle derives from position around home
                // every tick; the pulse merely surges the orbit speed via _intentTimer.
                break;

            case MusicalRole.Lead:
                // Meander: turn the base heading up to ±120° instead of teleport-turning,
                // and start the weave swinging in a fresh direction.
                _leadHeadingDeg += (float)(_rng.NextDouble() * 240.0 - 120.0);
                _leadSwervePhase = _rng.Next(2) == 0 ? 0f : Mathf.PI;
                break;

            default: // Fallback (None/Rhythm): fresh heading from the full 360°.
                float a = (float)_rng.NextDouble() * Mathf.PI * 2f;
                _intentDir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                break;
        }
    }

    private Vector2 ComputeDesiredVelocity(Vector2 cur, float fdt, bool resting)
    {
        switch (_role)
        {
            case MusicalRole.Bass:
                return _intentDir * Mathf.Max(_bassChargeSpeed, _speed);

            case MusicalRole.Groove:
            {
                float burst = _roleProfile != null ? _roleProfile.burstDuration : 0.4f;
                float pause = _roleProfile != null ? _roleProfile.pauseDuration : 0.35f;
                _groovePhaseTimer -= fdt;
                if (_groovePhaseTimer <= 0f)
                {
                    _grooveBurstActive = !_grooveBurstActive;
                    _groovePhaseTimer = _grooveBurstActive ? burst : pause;
                }
                return _grooveBurstActive ? _intentDir * _speed : Vector2.zero;
            }

            case MusicalRole.Harmony:
            {
                // Anchored ring orbit: tangential drive around home plus a radial spring
                // toward the ring. Deriving the angle from position (not an integrated
                // heading) is what keeps the circle legible after bounces and flees.
                Vector2 fromHome = cur - _homeWorld;
                if (fromHome.sqrMagnitude < 0.0001f)
                    fromHome = new Vector2(0.01f, 0f);
                float ringRadius = Mathf.Max(_homeFreeRadius, 0.5f);
                float dist = fromHome.magnitude;
                Vector2 radialDir = fromHome / dist;
                Vector2 tangentDir = _orbitalChirality * new Vector2(-radialDir.y, radialDir.x);

                // Pulse: sweep the configured arc in exactly one note duration, so long
                // chords glide slowly and short notes dart. Rest: slow crawl.
                float arcDeg = _roleProfile != null ? _roleProfile.collectableOrbitArcDegreesPerNote : 240f;
                float restMul = _roleProfile != null ? _roleProfile.collectableOrbitRestSpeedMul : 0.35f;
                float surgeSpeed = _profileSpeedMul * (arcDeg * Mathf.Deg2Rad * ringRadius) / _intentInterval;
                float restSpeed = _speed * restMul;
                float speedNow = resting ? restSpeed : Mathf.Max(surgeSpeed, restSpeed);

                Vector2 desired = tangentDir * speedNow
                                + radialDir * ((ringRadius - dist) * HomePullPerUnit());
                _intentDir = tangentDir;
                return Vector2.ClampMagnitude(desired, Mathf.Max(speedNow, _speed) * 1.5f);
            }

            case MusicalRole.Lead:
            {
                // Serpentine weave: sinusoidal swerve across a drifting base heading,
                // paced so one note duration spans a fixed number of S-cycles. The pulse
                // speed is derived from the cage so each pulse sweeps a real excursion
                // (travelRadii × radius per note) instead of speed × duration crumbs.
                float swerveDeg = _roleProfile != null ? _roleProfile.collectableSwerveDegrees : 65f;
                float cycles = _roleProfile != null ? _roleProfile.collectableSwerveCyclesPerNote : 1.5f;
                float travelRadii = _roleProfile != null ? _roleProfile.collectableTravelRadiiPerNote : 1.5f;
                float period = _intentInterval / Mathf.Max(0.25f, cycles);
                _leadSwervePhase += (2f * Mathf.PI / period) * fdt;
                float ang = (_leadHeadingDeg + swerveDeg * Mathf.Sin(_leadSwervePhase)) * Mathf.Deg2Rad;
                _intentDir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                float pulseSpeed = Mathf.Max(_speed, _profileSpeedMul * travelRadii * _homeFreeRadius / _intentInterval);
                return _intentDir * pulseSpeed;
            }

            default: // Fallback (None/Rhythm): straight dart on the current heading.
                return _intentDir * _speed;
        }
    }

    private bool IsFullyTrapped(Vector2 worldPos, DrumTrack drums, CosmicDustGenerator dustGen)
    {
        if (drums == null || dustGen == null) return false;
        Vector2Int c = drums.WorldToGridPosition(worldPos);
        if (!dustGen.HasDustAt(c)) return false;
        for (int i = 0; i < kDirs8.Length; i++)
        {
            // Any open neighbor (or off-grid edge) is an escape route.
            if (!dustGen.HasDustAt(c + kDirs8[i]))
                return false;
        }
        return true;
    }

    private void ClampToViewportRb()
    {
        if (_rb == null) return;

        Vector2 mn, mx;
        if (_hasMoveBounds)
        {
            mn = _moveBoundsMin;
            mx = _moveBoundsMax;
        }
        else
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;
            const float pad = 0.4f;
            mn = (Vector2)_cam.ViewportToWorldPoint(Vector3.zero) + new Vector2(pad, pad);
            mx = (Vector2)_cam.ViewportToWorldPoint(Vector3.one) - new Vector2(pad, pad);
        }

        Vector2 pos = _rb.position;
        Vector2 vel = _rb.linearVelocity;
        bool clamped = false;

        if (pos.x < mn.x)
        {
            pos.x = mn.x; clamped = true;
            if (vel.x < 0f) vel.x = 0f;
            if (_intentDir.x < 0f) ReflectIntentHorizontal();
        }
        else if (pos.x > mx.x)
        {
            pos.x = mx.x; clamped = true;
            if (vel.x > 0f) vel.x = 0f;
            if (_intentDir.x > 0f) ReflectIntentHorizontal();
        }

        if (pos.y < mn.y)
        {
            pos.y = mn.y; clamped = true;
            if (vel.y < 0f) vel.y = 0f;
            if (_intentDir.y < 0f) ReflectIntentVertical();
        }
        else if (pos.y > mx.y)
        {
            pos.y = mx.y; clamped = true;
            if (vel.y > 0f) vel.y = 0f;
            if (_intentDir.y > 0f) ReflectIntentVertical();
        }

        if (clamped)
        {
            _rb.position = pos;
            _rb.linearVelocity = vel;
        }
    }

    private void ReflectIntentHorizontal()
    {
        _intentDir.x = -_intentDir.x;
        _leadHeadingDeg = 180f - _leadHeadingDeg;
    }

    private void ReflectIntentVertical()
    {
        _intentDir.y = -_intentDir.y;
        _bassChargeSign = _intentDir.y >= 0f ? 1 : -1;
        _leadHeadingDeg = -_leadHeadingDeg;
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

    // While a timeline-armed movement intent is live, the note plows through dust
    // instead of bouncing — the ghost pulse opens temporary gray corridors.
    private bool IsPlowWindowActive() => _intentTimer > 0f && !_inCarry && !_handled;

    private void TryPlowDustCell(CosmicDust dust)
    {
        if (dust == null) return;
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var gen = _gfm != null ? _gfm.dustGenerator : null;
        var drums = assignedInstrumentTrack != null ? assignedInstrumentTrack.drumTrack : null;
        if (gen == null || drums == null) return;

        Vector2Int cell = drums.WorldToGridPosition(dust.transform.position);
        gen.CarveCellPreserveGray(cell, arrivalCarveFadeSeconds, DustClearSource.CollectablePlow);
    }

    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (_rb == null && !TryGetComponent(out _rb)) return;

        var dust = coll.collider != null ? coll.collider.GetComponent<CosmicDust>() : null;
        if (dust != null)
        {
            if (IsPlowWindowActive())
            {
                TryPlowDustCell(dust);
                return;
            }

            Vector2 away = Vector2.zero;

            if (coll.contactCount > 0)
                away = coll.GetContact(0).normal;
            if (away.sqrMagnitude < 0.0001f)
                away = (_rb.position - (Vector2)coll.collider.bounds.center);
            if (away.sqrMagnitude < 0.0001f)
                away = UnityEngine.Random.insideUnitCircle;

            away.Normalize();
            _rb.AddForce(away * dustCollisionEnterImpulse, ForceMode2D.Impulse);
            _bounceRecoverTimer = _roleProfile != null ? _roleProfile.collectableBounceRecoverSeconds : 0.45f;
        }
    }

    private void OnCollisionStay2D(Collision2D coll)
    {
        if (_rb == null) return;
        if (coll.collider == null) return;
        var dust = coll.collider.GetComponent<CosmicDust>();
        if (dust == null) return;

        if (IsPlowWindowActive())
        {
            // Drive through the wall instead of being pushed out of it.
            TryPlowDustCell(dust);
            return;
        }

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
