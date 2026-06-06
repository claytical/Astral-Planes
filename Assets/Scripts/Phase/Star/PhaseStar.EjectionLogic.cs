using System.Linq;
using UnityEngine;

public partial class PhaseStar
{
    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (!coll.gameObject.TryGetComponent<Vehicle>(out _)) return;

        if (!_isArmed
            && _state == PhaseStarState.WaitingForPoke
            && !_ejectionInFlight)
        {
            HandleDisarmedVehicleHit(coll);
            return;
        }

        if (OwnTrackCollectablesInFlight())
        {
            Disarm(PhaseStarDisarmReason.CollectablesInFlight, _lockedTint);
            Trace("OnCollisionEnter2D: ignored poke because own-track collectables are still in flight");
            return;
        }

        if (AnyExpansionPendingGlobal())
        {
            Trace("OnCollisionEnter2D: expansion pending — ignoring poke, star stays armed");
            return;
        }

        if (_state != PhaseStarState.WaitingForPoke)
        {
            Trace($"OnCollision: ignored, state={_state}");
            return;
        }

        if (_ejectionInFlight)
        {
            Trace("OnCollision: ignored, busy flags");
            return;
        }
        if (!IsEjectionReady())
        {
            if (GetDominantRoleRaw(out var dominantRoleForDescriptor, out _, out _))
            {
                var dominantTrack = FindTrackByRole(dominantRoleForDescriptor);
                if (dominantTrack != null)
                {
                    TryRefreshRequiredZapCountForPlannedRole(
                        dominantRoleForDescriptor,
                        dominantTrack,
                        resetCurrentZapCount: false,
                        reason: "collision-recovery-refresh");
                }
            }

            if (HasDominantRoleEjectable() && GetDominantRoleRaw(out var dominantRole, out _, out _))
            {
                TransitionZapState(ZapProgressState.ReadyLatched, dominantRole, "collision-recovery-latch");
            }
            else
            {
                Trace("OnCollisionEnter2D: ignored poke — dominant role not ejectable yet");
                return;
            }
        }
        if (_activeNode != null)
        {
            Trace("OnCollision: ignored, activeNode != null");
            return;
        }

        if (Time.frameCount == _lastPokeFrame)
        {
            Trace("OnCollision: ignored, same frame");
            return;
        }

        _lastPokeFrame = Time.frameCount;

        if (_cachedTrack == null)
            PrepareNextDirective();
        if (_cachedTrack == null)
        {
            Trace("OnCollision: no directive/track → disarm and wait");
            Disarm(PhaseStarDisarmReason.NodeResolving, _lockedTint);
            return;
        }

        // Block expansion-triggering ejections while any notes are still live
        // — carried by a vehicle, or floating unresolved from any track.
        if (_cachedTrack.GetBinCursor() >= _cachedTrack.loopMultiplier
            && (Vehicle.AnyVehicleCarryingTrack(_cachedTrack)
                || Collectable.AnyLiveForTrack(_cachedTrack)
                || Collectable.AnyLiveFromOtherTracks(_cachedTrack)))
        {
            Trace("OnCollisionEnter2D: new-bin expansion blocked — notes still in play");
            return;
        }

        if (_previewRole != MusicalRole.None)
        {
            if (_disarmReason == PhaseStarDisarmReason.SiblingActive) return;
            EjectActivePreviewShardAndFlow(coll);
            return;
        }

        EjectCachedDirectiveAndFlow(coll);
    }

    private void HandleDisarmedVehicleHit(Collision2D coll)
    {
        if (motion != null)
        {
            Vector2 starPos   = transform.position;
            Vector2 pushDir   = coll.contacts.Length > 0
                ? (starPos - (Vector2)coll.contacts[0].point).normalized
                : ((starPos - (Vector2)coll.transform.position).normalized);
            float   pushSpeed = Mathf.Clamp(coll.relativeVelocity.magnitude * disarmedPushScale,
                                             0f, MaxImpactStrength);
            motion.ApplyPushImpulse(pushDir * pushSpeed);
        }
        visuals?.FlashReject();
    }

    void SpawnNodeCommon(Vector2 contactPoint, InstrumentTrack usedTrack)
    {
        LastNodeWasSuperNode = false;
        LastNodeWasExpired   = false;
        LastNodeWasEscaped   = false;
        LastNodeWasCaptured  = false;
        int ticket = ++_spawnTicket;
        _ejectionInFlight = true;
        visuals?.EjectParticles(behaviorProfile?.ejectionPrefab);

        Color spawnTint = _previewVisual != null ? _previewColor : usedTrack.trackColor;
        _lockedTint = spawnTint;

        var node = DirectSpawnMineNode(contactPoint, usedTrack, spawnTint);
        if (node == null)
        {
            _ejectionInFlight = false;
            _activeNode = null;
            Disarm(PhaseStarDisarmReason.NodeResolving, spawnTint);
            return;
        }

        _activeNode = node;
        _ejectionInFlight = false;

        bool handledResolve = false;

        node.OnResolved += (_, outcome) =>
        {
            if (ticket != _spawnTicket) return;
            if (handledResolve) return;
            handledResolve = true;

            _activeNode = null;

            LastNodeWasCaptured = outcome == MineNodeOutcome.Captured;
            LastNodeWasEscaped  = outcome == MineNodeOutcome.Escaped;
            LastNodeWasExpired  = outcome == MineNodeOutcome.Expired;

            OnMineNodeResolved?.Invoke(this, _attunedRole);

            if (this == null) return;

            if (_state == PhaseStarState.BridgeInProgress) return;

            _awaitingCollectableClear = true;
            ResolveGameFlowManager();
            _awaitingCollectableClearSinceLoop = (_drum != null)
                ? _drum.completedLoops
                : (_gfm?.activeDrumTrack?.completedLoops ?? -1);
            _awaitingCollectableClearSinceDsp = AudioSettings.dspTime;
            HideInPlaceForBurst();
            Disarm(PhaseStarDisarmReason.NodeResolving, spawnTint);
            LogState("OnResolved");
        };

        CollectionSoundManager.Instance?.PlayPhaseStarImpact(usedTrack, usedTrack.GetCurrentNoteSet(), 0.8f);
        PrepareNextDirective();
        Trace("SpawnNodeCommon: end");
    }

    void EjectActivePreviewShardAndFlow(Collision2D coll)
    {
        if (behaviorProfile == null || visuals == null) return;

        if (!GetDominantRoleRaw(out MusicalRole ejectedRole, out float rawCharge, out float threshold))
            return;

        if (rawCharge < threshold)
            return;

        InstrumentTrack ejectedTrack = FindTrackByRole(ejectedRole);
        if (ejectedTrack == null)
        {
            Debug.LogError($"[PhaseStar] Missing track for ejected role={ejectedRole} (cannot spawn node).");
            return;
        }

        _starCharge[ejectedRole] = Mathf.Max(0f, rawCharge - threshold);
        _displayedCharge01 = 0f;
        var contact = coll.GetContact(0).point;
        var starPos = (Vector2)transform.position;
        var vehiclePos = coll.rigidbody != null ? coll.rigidbody.position : contact;

        Vector2 incoming = (starPos - vehiclePos);
        _lastImpactDir = (incoming.sqrMagnitude > 0.0001f) ? incoming.normalized : Vector2.right;
        _lastImpactStrength = Mathf.Clamp(coll.relativeVelocity.magnitude, 0f, MaxImpactStrength);

        visuals?.HideAll();
        dust?.ResetTentacles();

        Disarm(PhaseStarDisarmReason.NodeResolving, ejectedTrack.trackColor);

        if (GameFlowManager.VerboseLogging) Debug.Log($"[MNDBG] EjectActive: contact={contact}, role={ejectedTrack.assignedRole}");
        TransitionZapState(ZapProgressState.Ejecting, ejectedRole, "spawn-start");
        if (ShouldSpawnSuperNodeForTrack(ejectedTrack))
            SpawnSuperNodeCommon(contact, ejectedTrack);
        else
            SpawnNodeCommon(contact, ejectedTrack);
        if (_activeNode != null || _activeSuperNode != null)
        {
            TransitionZapState(ZapProgressState.Seeking, ejectedRole, "ejection-succeeded");
            dust?.SetAcquisitionEnabled(true, "post-eject-new-cycle");
        }

        OnEjected?.Invoke(this, ejectedRole);
    }

    private void SpawnSuperNodeCommon(Vector2 contactWorld, InstrumentTrack targetTrack)
    {
        LastNodeWasSuperNode = true;
        if (superNodePrefab == null)
        {
            Debug.LogError("[PhaseStar] superNodePrefab is null.");
            return;
        }

        if (soloVoice == null)
        {
            soloVoice = FindAnyObjectByType<SoloVoice>();
            if (soloVoice == null)
            {
                Debug.LogError("[PhaseStar] SoloVoice not found.");
                return;
            }
        }

        var go = Instantiate(superNodePrefab, contactWorld, Quaternion.identity);

        var sn = go.GetComponent<SuperNode>();
        if (sn == null)
        {
            Debug.LogError("[PhaseStar] SuperNode prefab missing SuperNode component.");
            return;
        }
        Color spawnTint = targetTrack != null ? targetTrack.trackColor : Color.white;

        var activeRoles = _assignedMotif?.GetActiveRoles() ?? new System.Collections.Generic.List<MusicalRole>();
        var ctrl        = _gfm?.controller;
        var shardTracks = new System.Collections.Generic.List<InstrumentTrack>();
        if (ctrl?.tracks != null)
            foreach (var t in ctrl.tracks)
                if (t != null && t != targetTrack
                    && activeRoles.Contains(t.assignedRole)
                    && t.loopMultiplier < t.maxLoopMultiplier)
                    shardTracks.Add(t);

        var alternateProg = _assignedMotif?.alternateChordProgressionProfile;

        var starPool = _gfm != null ? FindAnyObjectByType<StarPool>() : null;
        int captured   = starPool?.NodesCapturedThisMotif ?? 0;
        int total      = Mathf.Max(1, _assignedMotif?.nodesPerStar ?? 1);
        float difficulty01 = 1f - Mathf.Clamp01((float)captured / total);

        sn.Initialize(soloVoice, _drum, targetTrack, shardTracks, alternateProg, difficulty01);
        go.GetComponent<Explode>()?.SetTint(spawnTint);

        _activeSuperNode = sn;
        sn.OnResolved += () =>
        {
            _activeSuperNode = null;

            OnMineNodeResolved?.Invoke(this, _attunedRole);

            if (this == null) return;

            if (_state == PhaseStarState.BridgeInProgress) return;
            _awaitingCollectableClear = true;
            ResolveGameFlowManager();
            _awaitingCollectableClearSinceLoop = (_drum != null)
                ? _drum.completedLoops
                : (_gfm?.activeDrumTrack?.completedLoops ?? -1);
            _awaitingCollectableClearSinceDsp = AudioSettings.dspTime;
            HideInPlaceForBurst();
            Disarm(PhaseStarDisarmReason.NodeResolving, spawnTint);
            LogState("OnSuperNodeResolved");
        };

        PrepareNextDirective();
    }

    void EjectCachedDirectiveAndFlow(Collision2D coll)
    {
        var contact = coll.GetContact(0).point;
        var starPos = (Vector2)transform.position;
        var vehiclePos = coll.rigidbody != null ? coll.rigidbody.position : contact;

        _lastImpactDir = (starPos - vehiclePos).normalized;
        _lastImpactStrength = Mathf.Clamp(coll.relativeVelocity.magnitude, 0f, MaxImpactStrength);

        Disarm(PhaseStarDisarmReason.NodeResolving, _cachedTrack.trackColor);
        ActivateSafetyBubble();
        if (_cachedIsSuperNode)
            SpawnSuperNodeCommon(contact, _cachedTrack);
        else
            SpawnNodeCommon(contact, _cachedTrack);

        if (_activeNode != null || _activeSuperNode != null)
        {
            TransitionZapState(ZapProgressState.Seeking, _attunedRole, "ejection-succeeded");
            dust?.SetAcquisitionEnabled(true, "post-eject-new-cycle");
        }

        OnEjected?.Invoke(this, _attunedRole);
    }

    private bool ShouldSpawnSuperNodeForTrack(InstrumentTrack track)
    {
        if (track == null) return false;
        if (_assignedMotif?.alternateChordProgressionProfile == null) return false;

        int  maxBins       = Mathf.Max(1, track.maxLoopMultiplier);
        bool fullyExpanded = track.loopMultiplier >= maxBins;

        if (!fullyExpanded)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log(
                $"[SuperNodeGate] NO: track={track.name} role={track.assignedRole} loopMul={track.loopMultiplier} maxBins={maxBins}"
            );
            return false;
        }

        // Only spawn if there is at least one other active-role track still below max.
        // If all tracks are already maxed, CheckAndTriggerAllTracksMaxed should handle completion.
        var activeRoles = _assignedMotif.GetActiveRoles();
        var ctrl = _gfm?.controller;
        bool anyShards = false;
        if (ctrl?.tracks != null)
            foreach (var t in ctrl.tracks)
                if (t != null && t != track && activeRoles.Contains(t.assignedRole) && t.loopMultiplier < t.maxLoopMultiplier)
                { anyShards = true; break; }

        if (GameFlowManager.VerboseLogging) Debug.Log(
            $"[SuperNodeGate] {(anyShards ? "YES" : "NO (no shards available)")}: " +
            $"track={track.name} role={track.assignedRole} loopMul={track.loopMultiplier} maxBins={maxBins}"
        );

        return anyShards;
    }

    private MineNode DirectSpawnMineNode(Vector3 spawnFrom, InstrumentTrack track, Color color)
    {
        if (track == null || _drum == null) return null;

        Vector2Int cell = _drum.GetRandomAvailableCell();
        if (cell.x < 0) return null;
        _drum.OccupySpawnCell(cell.x, cell.y, GridObjectType.Node);
        var go = Instantiate(_drum.mineNodePrefab, spawnFrom, Quaternion.identity);
        var node = go.GetComponent<MineNode>();
        if (!node)
        {
            Destroy(go);
            _drum.FreeSpawnCell(cell.x, cell.y);
            return null;
        }

        var sr = go.GetComponentInChildren<SpriteRenderer>();
        if (sr) sr.color = color;
        var rb = go.GetComponent<Rigidbody2D>();
        if (rb != null && _lastImpactDir.sqrMagnitude > 0.0001f && _lastImpactStrength > 0f)
        {
            rb.linearVelocity = _lastImpactDir * _lastImpactStrength;
        }
        int entropy = CurrentEntropyForSelection();
        ResolveGameFlowManager();
        var noteSet = _gfm != null ? _gfm.GenerateNotes(track, entropy) : null;
        if (!TryResolveAuthoritativeZapCount(track.assignedRole, track, out _currentBurstRequiredZaps))
            _currentBurstRequiredZaps = Mathf.Max(1, GetNoteSetNoteCount(noteSet));
        if (GameFlowManager.VerboseLogging) Debug.Log($"[MineNode] Initializing track {track.name} with {track.assignedRole}");
        node.Initialize(track, noteSet, color, cell, diamondSprite: visuals?.diamond);
        return node;
    }

    private bool TryResolveAuthoritativeZapCount(MusicalRole role, InstrumentTrack track, out int noteCount)
    {
        noteCount = 0;
        if (_assignedMotif == null || track == null || role == MusicalRole.None)
        {
            Debug.LogWarning($"[PhaseStar:ZapCount] Early exit: motif={_assignedMotif != null} track={track != null} role={role}");
            return false;
        }

        int totalBins = Mathf.Max(1, track.maxLoopMultiplier);
        var cfg = _assignedMotif.GetConfigForRoleAtBin(role, 0, totalBins, track.voiceIndex);
        if (cfg == null)
        {
            Debug.LogWarning($"[PhaseStar:ZapCount] cfg null for role={role} voiceIndex={track.voiceIndex} totalBins={totalBins} motif={_assignedMotif.name}");
            return false;
        }

        if (cfg.riff != null && cfg.riff.riff.events != null && cfg.riff.riff.events.Count > 0)
        {
            noteCount = cfg.riff.riff.events.Count;
            if (GameFlowManager.VerboseLogging) Debug.Log($"[PhaseStar:ZapCount] role={role} voiceIndex={track.voiceIndex} riff={cfg.riff.name} events={noteCount}");
            return true;
        }

        Debug.LogWarning($"[PhaseStar:ZapCount] riff empty/null for role={role} cfg={cfg} riff={cfg.riff?.name ?? "null"}");
        return false;
    }

    private static int GetNoteSetNoteCount(NoteSet noteSet)
    {
        if (noteSet == null) return 0;
        int persistentTemplateCount = noteSet.persistentTemplate != null ? noteSet.persistentTemplate.Count : 0;
        int distinctStepCount = noteSet.GetStepList()?.Distinct().Count() ?? 0;
        int noteListCount = noteSet.GetNoteList()?.Count ?? 0;
        return Mathf.Max(persistentTemplateCount, Mathf.Max(distinctStepCount, noteListCount));
    }

    private void EnableColliders()
    {
        if (_isDisposing || this == null) return;
        var cols = GetComponentsInChildren<Collider2D>(true);
        foreach (var c in cols)
        {
            if (!c) continue;
            c.enabled = true;
        }
    }

    private void DisableColliders()
    {
        if (_isDisposing || this == null) return;
        var cols = GetComponentsInChildren<Collider2D>(true);
        foreach (var c in cols)
        {
            if (!c) continue;
            c.enabled = false;
        }
    }
}
