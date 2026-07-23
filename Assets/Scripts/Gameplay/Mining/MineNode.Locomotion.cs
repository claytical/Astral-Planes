using UnityEngine;

public partial class MineNode
{
    private void ApplyLocomotion(float speedIntensity01, float speedScale)
    {
        var profile = _activeLocomotionProfile;
        if (profile == null)
        {
            float fallback = Mathf.Max(Mathf.Lerp(0.5f, 2.5f, _speed) * speedScale, kMinSpeedFloor);
            Vector2 fallbackForce = (_carveDir * fallback - _rb.linearVelocity) * Mathf.Lerp(3f, 20f, _speed) * _rb.mass;
            _rb.AddForce(fallbackForce, ForceMode2D.Force);
            _currentDesiredSpeed = fallback;
            return;
        }

        float targetSpeed = Mathf.Max(profile.EvaluateTargetSpeed(speedIntensity01) * speedScale, kMinSpeedFloor);
        if (_dustInteractor == null) _dustInteractor = GetComponent<MineNodeDustInteractor>();
        if (_dustInteractor != null && _dustInteractor.IsInDustAtCurrentCell())
        {
            float drag = Mathf.Clamp01(_dustInteractor.dustDragScalar);
            float penalty = Mathf.Clamp01(profile.dustPenalty);
            targetSpeed *= Mathf.Lerp(1f, drag, penalty);
        }

        _currentDesiredSpeed = targetSpeed;

        Vector2 desiredVelocity = _carveDir * targetSpeed;
        float turnBlend = 1f - Mathf.Clamp01(profile.hesitation);
        if (_rb.linearVelocity.sqrMagnitude > 0.001f)
            desiredVelocity = Vector2.Lerp(_rb.linearVelocity, desiredVelocity, Mathf.Clamp01(profile.turnRate * turnBlend * Time.fixedDeltaTime));
        Vector2 delta = desiredVelocity - _rb.linearVelocity;
        Vector2 accelForce = Vector2.ClampMagnitude(delta * profile.acceleration * _rb.mass, profile.maxSpeed * _rb.mass);
        _rb.AddForce(accelForce, ForceMode2D.Force);

        if (_rb.linearVelocity.magnitude > targetSpeed)
            _rb.AddForce(-_rb.linearVelocity * profile.braking, ForceMode2D.Force);
    }

    private MineNodeLocomotionProfile ResolveLocomotionProfile(MusicalRoleProfile roleProfile)
    {
        if (roleProfile != null && roleProfile.mineNodeLocomotionProfile != null)
            return roleProfile.mineNodeLocomotionProfile;

        int count = locomotionArchetypeProfiles != null ? locomotionArchetypeProfiles.Length : 0;
        if (count > 0)
        {
            int idx = Mathf.Clamp(Mathf.RoundToInt(_speed * (count - 1)), 0, count - 1);
            if (locomotionArchetypeProfiles[idx] != null)
                return locomotionArchetypeProfiles[idx];
        }
        return defaultLocomotionProfile;
    }

    private void RunCorridorLookahead(Vector2Int myCell)
    {
        if (Time.time < _nextDirectionDecisionAt || Time.time < _pathCommitUntil)
        {
            if (_behaviorIntent != MineNodeBehaviorIntent.Committing)
                SetBehaviorIntent(MineNodeBehaviorIntent.Committing);
            return;
        }

        if (_behaviorIntent != MineNodeBehaviorIntent.Thinking)
            SetBehaviorIntent(MineNodeBehaviorIntent.Thinking);

        _rescanTimer -= Time.fixedDeltaTime;

        bool wallAhead = false;
        for (int i = 1; i <= 3; i++)
        {
            var probe = myCell + new Vector2Int(Mathf.RoundToInt(_carveDir.x * i), Mathf.RoundToInt(_carveDir.y * i));
            if (_drumTrack.HasDustAt(probe)) { wallAhead = true; break; }
        }

        if (!wallAhead)
        {
            Vector2 velN = _rb.linearVelocity.sqrMagnitude > 0.04f ? _rb.linearVelocity.normalized : Vector2.zero;
            if (velN.sqrMagnitude > 0.0001f)
            {
                for (int i = 1; i <= 2; i++)
                {
                    var probe = myCell + new Vector2Int(Mathf.RoundToInt(velN.x * i), Mathf.RoundToInt(velN.y * i));
                    if (_drumTrack.HasDustAt(probe)) { wallAhead = true; break; }
                }
            }
        }

        if (wallAhead || _rescanTimer <= 0f)
        {
            _rescanTimer = 0.6f;
            Vector2[] candidates =
            {
                Vector2.right,
                new Vector2( 1f,  1f).normalized,
                Vector2.up,
                new Vector2(-1f,  1f).normalized,
                Vector2.left,
                new Vector2(-1f, -1f).normalized,
                Vector2.down,
                new Vector2( 1f, -1f).normalized,
            };

            float bestScore = float.MinValue;
            Vector2 bestDir = _carveDir;
            foreach (var dir in candidates)
            {
                float score = 0f;
                for (int i = 1; i <= 3; i++)
                {
                    var probe = myCell + new Vector2Int(Mathf.RoundToInt(dir.x * i), Mathf.RoundToInt(dir.y * i));
                    if (!_drumTrack.HasDustAt(probe)) score += 1f;
                    else break;
                }
                for (int d = 1; d <= 3; d++)
                {
                    var lc = myCell + new Vector2Int(Mathf.RoundToInt(-dir.y * d), Mathf.RoundToInt( dir.x * d));
                    var rc = myCell + new Vector2Int(Mathf.RoundToInt( dir.y * d), Mathf.RoundToInt(-dir.x * d));
                    if (_drumTrack.HasDustAt(lc) || _drumTrack.HasDustAt(rc))
                    {
                        score += 1.5f / d;
                        break;
                    }
                }
                float riskBias = Mathf.Lerp(-0.7f, 0.7f, _decisionArchetype.dustRiskTolerance);
                score += riskBias * Mathf.Clamp01(score / 3f);
                // Territory affinity
                if (_dustGenerator != null)
                {
                    const float affinityWeight = 0.3f;
                    var affinityProbe = myCell + new Vector2Int(Mathf.RoundToInt(dir.x * 2), Mathf.RoundToInt(dir.y * 2));
                    if (_dustGenerator.GetZoneRole(affinityProbe) == _role) score += affinityWeight;
                }
                // Orbital bias (Harmony): 2.0 base + profile fine-tune; competes meaningfully with clearance scores
                if (_behaviorCategory == MineNodeBehaviorCategory.Orbital)
                {
                    float orbitalBias = 2.0f + (_roleProfile?.orbitalTurnBias ?? 0.6f);
                    var perp = new Vector2(-_carveDir.y * _orbitSign, _carveDir.x * _orbitSign).normalized;
                    score += orbitalBias * Mathf.Max(0f, Vector2.Dot(dir.normalized, perp));
                }
                // Proximity evasion (Lead/Darting): 7-cell category default; profile overrides if > 0
                if (_behaviorCategory == MineNodeBehaviorCategory.Darting && _trackedVehicle != null)
                {
                    float cells = (_roleProfile != null && _roleProfile.evasionCells > 0f) ? _roleProfile.evasionCells : 7f;
                    float worldRadius = cells * GetCellSize();
                    float dist = Vector2.Distance(_rb.position, _trackedVehicle.transform.position);
                    if (dist < worldRadius)
                    {
                        Vector2 away = (_rb.position - (Vector2)_trackedVehicle.transform.position).normalized;
                        score += 0.6f * Mathf.Max(0f, Vector2.Dot(dir.normalized, away));
                    }
                }
                if (Vector2.Dot(dir, _carveDir) < -0.5f) score *= 0.1f;
                if (score > bestScore) { bestScore = score; bestDir = dir; }
            }
            float jitter = UnityEngine.Random.Range(-_decisionArchetype.turnJitter, _decisionArchetype.turnJitter);
            _carveDir = Rotate(bestDir.normalized, jitter).normalized;
            _nextDirectionDecisionAt = Time.time + _decisionArchetype.SampleReactionDelay();
            _pathCommitUntil = Time.time + Mathf.Max(0.05f, _decisionArchetype.pathCommitmentDuration * CategoryCommitScale());
            SetBehaviorIntent(MineNodeBehaviorIntent.Committing);
        }
    }

    // Returns the commit-duration multiplier for this category.
    // Deliberate (Bass): 2.5x base. Darting (Lead): 0.35x base. Others: 1x.
    private float CategoryCommitScale()
    {
        return _behaviorCategory switch {
            MineNodeBehaviorCategory.Deliberate => 2.5f,
            MineNodeBehaviorCategory.Darting    => 0.35f,
            _                                   => 1f,
        };
    }

    private float GetCellSize()
    {
        if (_drumTrack != null)
            return Vector2.Distance(
                _drumTrack.GridToWorldPosition(Vector2Int.zero),
                _drumTrack.GridToWorldPosition(Vector2Int.right));
        return 1f;
    }

    private void RunStallEscape(Vector2Int myCell)
    {
        float vMag  = _rb.linearVelocity.magnitude;
        float align = (vMag > 0.0001f) ? Vector2.Dot(_rb.linearVelocity.normalized, _carveDir) : 0f;
        bool pressedNow = (vMag < kStallSpeed) || (align < kStuckDot);

        bool confirmedStall = false;
        if (Time.time >= _nextStallSampleTime)
        {
            Vector2 pos   = _rb.position;
            float   moved = (pos - _lastPosForStall).magnitude;
            if (_nextStallSampleTime > 0f)
            {
                if (moved < kStallDistanceEps && pressedNow) _stallHits++;
                else _stallHits = Mathf.Max(0, _stallHits - 1);
                confirmedStall = (_stallHits >= 1);
            }
            _lastPosForStall     = pos;
            _nextStallSampleTime = Time.time + kStallSamplePeriod;
        }

        if (!confirmedStall || Time.time < _nextEscapeAllowedTime) return;

        float recoveryScale = Mathf.Lerp(1.25f, 0.4f, _decisionArchetype.stallRecoveryAggressiveness);
        _nextEscapeAllowedTime = Time.time + (kEscapeCooldown * recoveryScale);
        _stallHits = 0;

        Vector2 fwd   = (_carveDir.sqrMagnitude > 0.001f) ? _carveDir.normalized : Vector2.right;
        Vector2 left  = new Vector2(-fwd.y,  fwd.x);
        Vector2 right = new Vector2( fwd.y, -fwd.x);
        int w = _drumTrack.GetSpawnGridWidth();
        int h = _drumTrack.GetSpawnGridHeight();
        Vector2 toCenter = new Vector2(w * 0.5f - myCell.x, h * 0.5f - myCell.y);
        if (toCenter.sqrMagnitude > 0.001f) toCenter.Normalize();

        float lDot = Vector2.Dot(left,  toCenter);
        float rDot = Vector2.Dot(right, toCenter);
        _carveDir = (lDot > rDot) ? left : right;

        if (vMag > 0.05f)
        {
            Vector2 vN = _rb.linearVelocity.normalized;
            float l = Mathf.Abs(Vector2.Dot(vN, left));
            float r = Mathf.Abs(Vector2.Dot(vN, right));
            _carveDir = (l < r) ? left : right;
        }
        else
        {
            _carveDir = Rotate(fwd, UnityEngine.Random.Range(150f, 210f)).normalized;
        }

        float jitter = Mathf.Lerp(kEscapeJitterDeg * 0.5f, kEscapeJitterDeg * 1.5f, _decisionArchetype.stallRecoveryAggressiveness);
        _carveDir = Rotate(_carveDir, UnityEngine.Random.Range(-jitter, jitter)).normalized;
        _rb.linearVelocity *= 0.5f;
    }

    private void ResolveDecisionArchetype()
    {
        _decisionArchetype = default;
        if (decisionArchetypeLibrary == null)
        {
            _decisionArchetype.id = "Steady";
            _decisionArchetype.reactionDelayWindow = new Vector2(0.1f, 0.25f);
            _decisionArchetype.pathCommitmentDuration = 0.75f;
            _decisionArchetype.turnJitter = 8f;
            _decisionArchetype.fleeBias = 0.6f;
            _decisionArchetype.stallRecoveryAggressiveness = 0.6f;
            _decisionArchetype.dustRiskTolerance = 0.5f;
            return;
        }

        decisionArchetypeLibrary.TryGet("Steady", out _decisionArchetype);
    }

    private void CacheAuthoredStepsFromNoteSet()
    {
        _stepsPerLoop = 16;
        if (_drumTrack == null || _noteSet == null) return;
        var steps = _noteSet.GetStepList();
        if (steps == null || steps.Count == 0) return;
        int max = -1;
        for (int i = 0; i < steps.Count; i++) max = Mathf.Max(max, steps[i]);
        _stepsPerLoop = Mathf.Max(1, max + 1);
        int bar = 16;
        if (_stepsPerLoop % bar != 0) _stepsPerLoop = ((_stepsPerLoop / bar) + 1) * bar;
    }

    public void ReflectCarveDir(bool reflectX)
    {
        if (reflectX) _carveDir.x = -_carveDir.x;
        else          _carveDir.y = -_carveDir.y;
    }

    private static Vector2 Rotate(Vector2 v, float degrees)
    {
        float r = degrees * Mathf.Deg2Rad;
        float c = Mathf.Cos(r);
        float s = Mathf.Sin(r);
        return new Vector2(c * v.x - s * v.y, s * v.x + c * v.y);
    }
}
