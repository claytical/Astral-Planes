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
            Debug.Log($"[PS:SILENT] {name} role={_attunedRole} reason=expansion-pending " +
                      $"anyVehicleCarrying={Vehicle.AnyVehicleCarrying()} pendingTracks=({DebugExpansionPendingTracks()})");
            return;
        }

        if (_state != PhaseStarState.WaitingForPoke)
        {
            Trace($"OnCollision: ignored, state={_state}");
            Debug.Log($"[PS:SILENT] {name} role={_attunedRole} reason=bad-state state={_state} isArmed={_isArmed} ejectionInFlight={_ejectionInFlight}");
            return;
        }

        if (_ejectionInFlight)
        {
            Trace("OnCollision: ignored, busy flags");
            Debug.Log($"[PS:SILENT] {name} role={_attunedRole} reason=ejection-in-flight " +
                      $"activeNode={(_activeNode ? _activeNode.name : "null")} activeSuperNode={(_activeSuperNode ? _activeSuperNode.name : "null")}");
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
                Debug.Log($"[PS:SILENT] {name} role={_attunedRole} reason=dominant-not-ejectable " +
                          $"zapState={_zapProgressState} dominantRole={dominantRoleForDescriptor} charge01={GetChargeNormalized01(dominantRoleForDescriptor):F2}");
                return;
            }
        }
        if (_activeNode != null)
        {
            Trace("OnCollision: ignored, activeNode != null");
            Debug.Log($"[PS:SILENT] {name} role={_attunedRole} reason=active-node activeNode={_activeNode.name}");
            return;
        }

        if (Time.frameCount == _lastPokeFrame)
        {
            Trace("OnCollision: ignored, same frame");
            Debug.Log($"[PS:SILENT] {name} role={_attunedRole} reason=same-frame frame={Time.frameCount}");
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
            Debug.Log($"[PS:SILENT] {name} role={_attunedRole} reason=bin-full-notes-in-play " +
                      $"track={_cachedTrack.name} binCursor={_cachedTrack.GetBinCursor()} loopMultiplier={_cachedTrack.loopMultiplier} " +
                      $"vehicleCarryingThisTrack={Vehicle.AnyVehicleCarryingTrack(_cachedTrack)} " +
                      $"liveOnThisTrack={Collectable.AnyLiveForTrack(_cachedTrack)} " +
                      $"liveOnOtherTracks={Collectable.AnyLiveFromOtherTracks(_cachedTrack)}");
            return;
        }

        if (_previewRole != MusicalRole.None)
        {
            if (_disarmReason == PhaseStarDisarmReason.SiblingActive)
            {
                Debug.Log($"[PS:SILENT] {name} role={_attunedRole} reason=sibling-active previewRole={_previewRole}");
                return;
            }
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

    bool SpawnNodeCommon(Vector2 contactPoint, InstrumentTrack usedTrack)
    {
        LastNodeWasSuperNode = false;
        LastNodeWasExpired   = false;
        LastNodeWasEscaped   = false;
        LastNodeWasCaptured  = false;
        int ticket = ++_spawnTicket;
        _ejectionInFlight = true;
        visuals?.EjectParticles(behaviorProfile?.ejectionPrefab);

        Color spawnTint = _previewVisual != null ? _previewColor : usedTrack.DisplayColor;
        _lockedTint = spawnTint;

        var node = DirectSpawnMineNode(contactPoint, usedTrack, spawnTint);
        if (node == null)
        {
            _ejectionInFlight = false;
            _activeNode = null;
            Debug.LogWarning($"[PhaseStar] MineNode spawn failed (no free grid cell) — recovering for retry. star={name} role={usedTrack.assignedRole}");
            return false;
        }

        _activeNode = node;
        _ejectionInFlight = false;

        // The node now holds the dust energy drained to build it; those cells regrow
        // when the node resolves or is destroyed.
        node.AttachHeldDustBatch(_heldDrainCells);
        _heldDrainCells.Clear();

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
        return true;
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

        bool isSuperNodeEjection = ShouldSpawnSuperNodeForTrack(ejectedTrack);
        if (CanCommitEjection != null && !CanCommitEjection(ejectedRole, isSuperNodeEjection))
        {
            // Another sequence is still unresolved (or the harvest budget is spent). Keep
            // the charge and stay armed — the poke retries once that sequence resolves.
            visuals?.FlashReject();
            if (GameFlowManager.VerboseLogging) Debug.Log($"[PhaseStar] Eject deferred — sequence in flight or no harvest budget (role={ejectedRole}).");
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

        Disarm(PhaseStarDisarmReason.NodeResolving, ejectedTrack.DisplayColor);

        if (GameFlowManager.VerboseLogging) Debug.Log($"[MNDBG] EjectActive: contact={contact}, role={ejectedTrack.assignedRole}");
        TransitionZapState(ZapProgressState.Ejecting, ejectedRole, "spawn-start");
        bool spawned = isSuperNodeEjection
            ? SpawnSuperNodeCommon(contact, ejectedTrack)
            : SpawnNodeCommon(contact, ejectedTrack);

        if (!spawned)
        {
            // Refund the charge deducted above and re-latch so the next poke retries.
            // OnEjected must NOT fire — StarPool would set _mineNodePending with no node
            // to ever resolve it, permanently reserving an ejection slot.
            _starCharge[ejectedRole] = rawCharge;
            TransitionZapState(ZapProgressState.ReadyLatched, ejectedRole, "spawn-failed-relatch");
            ArmNext();
            Trace("EjectActive: node spawn failed — star recovered, no OnEjected");
            return;
        }

        TransitionZapState(ZapProgressState.Seeking, ejectedRole, "ejection-succeeded");
        dust?.SetAcquisitionEnabled(true, "post-eject-new-cycle");

        OnEjected?.Invoke(this, ejectedRole);
    }

    private bool SpawnSuperNodeCommon(Vector2 contactWorld, InstrumentTrack targetTrack)
    {
        LastNodeWasSuperNode = true;
        if (superNodePrefab == null)
        {
            Debug.LogError("[PhaseStar] superNodePrefab is null.");
            return false;
        }

        if (soloVoice == null)
        {
            soloVoice = FindAnyObjectByType<SoloVoice>();
            if (soloVoice == null)
            {
                Debug.LogError("[PhaseStar] SoloVoice not found.");
                return false;
            }
        }

        var go = Instantiate(superNodePrefab, contactWorld, Quaternion.identity);

        var sn = go.GetComponent<SuperNode>();
        if (sn == null)
        {
            Debug.LogError("[PhaseStar] SuperNode prefab missing SuperNode component.");
            Destroy(go);
            return false;
        }
        Color spawnTint = targetTrack != null ? targetTrack.DisplayColor : Color.white;

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

        go.GetComponent<Explode>()?.SetTint(spawnTint);

        _activeSuperNode = sn;
        sn.OnResolved += () =>
        {
            _activeSuperNode = null;

            // Mirror SuperNodeTrackNode.Collect() for the initiating/fully-expanded track —
            // shard tracks (if any) handle their own InstantFillAllBins via their
            // SuperNodeTrackNode.Collect(), but targetTrack itself is never a shard, so it
            // needs the same "fill + advance" treatment here.
            if (targetTrack != null)
            {
                var rings = GameFlowManager.Instance?.GetMotifRingGlyphApplicator();
                rings?.SetSuperNodeMode(true);

                int pre = targetTrack.loopMultiplier;
                targetTrack.InstantFillAllBins(toMaxCapacity: true);
                int post = targetTrack.loopMultiplier;

                if (post > pre)
                {
                    int binSz = targetTrack.BinSize();
                    targetTrack.controller?.StartSuperNodeCompletionSequence(
                        targetTrack, pre, post, /*ascendLoopsOverride*/ 3, binSz, _drum);
                }
                else
                {
                    targetTrack.controller?.CheckAndTriggerAllTracksMaxed();
                }

                targetTrack.controller?.StageAltChordIfAllTracksMaxed();
            }

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

        // Initialize AFTER subscribing — when shardTracks is empty, Initialize() resolves
        // synchronously (FireResolvedOnce inside Initialize), and the subscriber above must
        // already be attached to receive that event.
        sn.Initialize(soloVoice, _drum, targetTrack, shardTracks, alternateProg, difficulty01);

        PrepareNextDirective();
        return true;
    }

    void EjectCachedDirectiveAndFlow(Collision2D coll)
    {
        if (CanCommitEjection != null && !CanCommitEjection(_attunedRole, _cachedIsSuperNode))
        {
            // Another sequence is still unresolved (or the harvest budget is spent). Keep
            // the cached directive and stay armed — the poke retries once it resolves.
            visuals?.FlashReject();
            if (GameFlowManager.VerboseLogging) Debug.Log($"[PhaseStar] Cached eject deferred — sequence in flight or no harvest budget (role={_attunedRole}).");
            return;
        }

        var contact = coll.GetContact(0).point;
        var starPos = (Vector2)transform.position;
        var vehiclePos = coll.rigidbody != null ? coll.rigidbody.position : contact;

        _lastImpactDir = (starPos - vehiclePos).normalized;
        _lastImpactStrength = Mathf.Clamp(coll.relativeVelocity.magnitude, 0f, MaxImpactStrength);

        Disarm(PhaseStarDisarmReason.NodeResolving, _cachedTrack.DisplayColor);
        ActivateSafetyBubble();
        bool spawned = _cachedIsSuperNode
            ? SpawnSuperNodeCommon(contact, _cachedTrack)
            : SpawnNodeCommon(contact, _cachedTrack);

        if (!spawned)
        {
            // Re-latch and re-arm so the next poke retries (ArmNext also deactivates the
            // safety bubble). OnEjected must NOT fire — see EjectActivePreviewShardAndFlow.
            TransitionZapState(ZapProgressState.ReadyLatched, _attunedRole, "spawn-failed-relatch");
            ArmNext();
            return;
        }

        TransitionZapState(ZapProgressState.Seeking, _attunedRole, "ejection-succeeded");
        dust?.SetAcquisitionEnabled(true, "post-eject-new-cycle");

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

        // Shard tracks (other active-role tracks still below max) are optional bonus content,
        // not a prerequisite — SuperNode fires once this track is fully expanded and an
        // alternate chord progression exists, even for single-active-role motifs.
        var activeRoles = _assignedMotif.GetActiveRoles();
        var ctrl = _gfm?.controller;
        bool anyShards = false;
        if (ctrl?.tracks != null)
            foreach (var t in ctrl.tracks)
                if (t != null && t != track && activeRoles.Contains(t.assignedRole) && t.loopMultiplier < t.maxLoopMultiplier)
                { anyShards = true; break; }

        if (GameFlowManager.VerboseLogging) Debug.Log(
            $"[SuperNodeGate] YES ({(anyShards ? "with shards" : "no shards available")}): " +
            $"track={track.name} role={track.assignedRole} loopMul={track.loopMultiplier} maxBins={maxBins}"
        );

        return true;
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
