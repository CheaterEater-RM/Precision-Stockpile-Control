using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // The store-search engine's read path (store-search rewrite, Phase 1).
    //
    // DELIBERATELY NOT a mirrored "hard record": mode / feeder participation / cap presence / fine-order
    // rank are already O(1) on PscStorageData and already invalidated by the existing seams
    // (NotifyPolicyChanged -> UpdateTracking, the tracked set, the anyXxxActive gates, the
    // NumberingGeneration-stamped rank cache that reads the band live, and the 250-tick resync). Duplicating
    // them into a second record would add an invalidation burden for no gain. Filter-allows is read LIVE (the
    // delegated vanilla AllowedToAccept enforces it during the engine's per-unit cell probe), never
    // summarized here. So the only state this owns is the SOFT def->units prefilter, held map-local on
    // PscMapComponent.admitIndex.
    //
    // The shared hard-admit predicate the engine and the reworked AllowedToAccept backstop will both call is
    // extracted here in Phase 2 (Task 2.3), when the backstop is reworked. Phase 1 is purely additive:
    // nothing reads this facade yet, so hauling behavior is unchanged.
    public static class PscAdmissionIndex
    {
        // ONE explicit rank source for the engine (do NOT trust AllGroupsListInPriorityOrder ordering as the
        // source of truth). PscOrder.RankWithinBand reads settings.Priority live, so a vanilla priority change
        // needs no extra invalidation. Lower = higher priority.
        public static int RankOf(StorageSettings s) => PscOrder.RankWithinBand(s);

        private static readonly IReadOnlyList<StorageSettings> Empty = new List<StorageSettings>();

        // Soft "maybe-accepts" prefilter: the MANAGED units whose filter allows `def` AND whose mode permits
        // intake. Def-level only; item-specific filter facets (rot / quality / HP / special filters) are
        // confirmed live by the delegated vanilla AllowedToAccept, NEVER here. Unmanaged groups are
        // pure-vanilla and reached via the engine's walk of AllGroupsListInPriorityOrder, so they are not
        // listed. At Precise the engine ignores this and walks the full eligible set, so a stale list can
        // never drop an admissible unit; Balanced/Performance narrowing semantics are finalized in Phase 3.
        //
        // CONTRACT (read carefully before Phase 2 consumes this):
        //  - The return is READ-ONLY. It is the live backing list inside admitIndex (or the shared Empty
        //    sentinel), handed out as IReadOnlyList so a consumer cannot Add/Sort/Clear it in place and
        //    corrupt the index. The engine MUST copy into its own buffer before ranking/filtering.
        //  - This is NOT "the candidate list." It is the managed-unit prefilter only. The Phase 2 engine
        //    still has to merge in unmanaged vanilla stockpiles (walked from AllGroupsListInPriorityOrder)
        //    before it has a complete candidate set. Never treat CandidateUnits alone as exhaustive.
        // Built but NOT yet authoritative in Phase 1.
        public static IReadOnlyList<StorageSettings> CandidateUnits(Map map, ThingDef def)
        {
            if (def == null) return Empty;
            var psc = PscMapComponent.For(map);
            if (psc == null) return Empty;
            return psc.admitIndex.TryGetValue(def, out var list) ? list : Empty;
        }

        // Rebuild the map-local prefilter from the component's tracked set. Called from the existing tracking
        // seams (UpdateTracking, RebuildTrackingFromStore, the resync backstop), not on a new generation
        // counter. Bounded by (managed units x allowed defs per unit); a handful of managed units is trivial.
        internal static void Rebuild(PscMapComponent psc)
        {
            if (psc == null) return;
            var index = psc.admitIndex;
            index.Clear();
            foreach (var s in psc.tracked)
            {
                var data = PscStorageDataStore.TryGet(s);
                if (data == null) continue;
                // Off / RetrieveOnly block haul-in: such a unit can never be an intake candidate.
                if (data.mode != PscStorageMode.Normal && data.mode != PscStorageMode.AcceptOnly) continue;
                var filter = s.filter;
                if (filter == null) continue;
                foreach (var def in filter.AllowedThingDefs)
                {
                    if (def == null) continue;
                    if (!index.TryGetValue(def, out var list))
                    {
                        list = new List<StorageSettings>();
                        index[def] = list;
                    }
                    list.Add(s);
                }
            }
            if (PscLog.Enabled)
                PscLog.Msg($"index: rebuilt map={psc.map.uniqueID} units={psc.tracked.Count} defs={index.Count}");
        }

        // ── Selection-level hard-admit predicate (store-search rewrite, Phase 2) ──────────────────────
        // The single hard-admit decision shared by the engine (planning: true) and the AllowedToAccept
        // backstop (planning: false). Returns true when the haul of `t` into `target` / `unit` must be
        // REJECTED (tighten-only: only ever turns a vanilla true into false), with a short `reason` for logs.
        //
        // `planning` selects the two concerns the old PscAdmissionScope.InStoreSearch flag conflated: the
        // effective-vs-physical count (GetEffectiveCount vs GetCount) AND the per-search memo routing inside
        // TryFeederReject. The engine passes true (planning a new haul: reserved-inbound counts, memo reused);
        // every other AllowedToAccept caller passes false (recheck: physical, fresh). The bypass that makes
        // the backstop early-out during the engine's own delegated cell probe is a SEPARATE thread-static
        // (PscEngineScope.BypassAdmissionBackstop) -- the two are never the same mechanism (see PscEngineScope).
        //
        // Inputs are pre-resolved by the caller (the engine reuses PscSearchContext; the backstop resolves
        // fresh), so this is pure logic over resolved values. These are exactly the checks that lived in
        // StorageSettings_AllowedToAccept_Patch.Postfix before the rewrite (DESIGN §8).
        internal static bool HardReject(StorageSettings target, Thing t, PscHaulUnit unit, PscHaulUnit source,
            PscStorageData sourceData, bool sourceIsTarget, bool planning, out string reason)
        {
            reason = null;
            PscStorageData data = PscStorageDataStore.TryGet(target);

            // Mode haul-in block (M5.2): an Off / RetrieveOnly unit accepts no new hauls. Target-keyed and
            // guarded by sourceIsTarget so the unit's own contents are never flagged as misplaced (D16).
            if (!sourceIsTarget && data != null &&
                (data.mode == PscStorageMode.Off || data.mode == PscStorageMode.RetrieveOnly))
            {
                reason = "modeNoIntake";
                if (PscLog.Enabled) LogReject(t, unit, reason,
                    $"mode: rejected {t.def.defName} -> {unit.UniqueLoadID} (mode {data.mode}, no intake)");
                return true;
            }

            // Feeder gates first (a source's onlyToDestinations blocks the haul even into a no-policy
            // target). data is reused by the limit gate when the feeder gate had to resolve it.
            if (!sourceIsTarget && TryFeederReject(target, t, unit, source, sourceData, ref data, planning))
            {
                reason = "feeder";
                return true;
            }

            // Batch empty (source-keyed): never let an item LEAVE its source unless this stack is at least
            // batchEmpty (the most one trip can remove). Same exemption as onlyToDestinations: a
            // disallowed/misplaced item the source no longer allows must stay evacuable.
            if (!sourceIsTarget && source.IsValid)
            {
                if (sourceData != null && sourceData.batchEmpty > 0 && t.stackCount < sourceData.batchEmpty
                    && source.Settings.AllowedToAccept(t))
                {
                    reason = "underBatchEmpty";
                    if (PscLog.Enabled) LogReject(t, source, reason,
                        $"batchEmpty: rejected {t.def.defName} leaving {source.UniqueLoadID} (source stack {t.stackCount} < batchEmpty {sourceData.batchEmpty})");
                    return true;
                }
            }

            // --- Limit / batch gates (M1/M2) ---
            data ??= PscStorageDataStore.TryGet(target);
            if (data == null) return false;
            bool hasLimit = data.HasLimit(t.def);
            if (!hasLimit && data.batch <= 0) return false;    // no effective limit and no batch -> vanilla

            // D16: a unit's own contents are normally always valid. The ONE documented exception (DESIGN §8):
            // contents STRICTLY over the per-def cap read as misplaced, so vanilla's normal hauling drains the
            // excess and stops EXACTLY at the cap. Physical count (GetCount, NOT planning) so an in-flight
            // reservation never makes own contents read over cap. The HaulToCellStorageJob drain clamp stops a
            // single hauler overshooting below the cap.
            if (sourceIsTarget)
            {
                if (hasLimit)
                {
                    var ownLim = data.GetLimit(t.def);
                    if (ownLim.Upper.HasValue)
                    {
                        int ownCount = data.GetCount(t.def, unit);
                        if (ownCount > ownLim.Upper.Value)
                        {
                            reason = "overCapDrain";
                            if (PscLog.Enabled) LogReject(t, unit, reason,
                                $"limit: draining over-cap {t.def.defName} from {unit.UniqueLoadID} ({ownCount} > cap {ownLim.Upper.Value})");
                            return true;
                        }
                    }
                }
                return false;                            // own contents otherwise always valid (D16)
            }

            if (hasLimit)
            {
                var lim = data.GetLimit(t.def);
                // Planning gate: effective = physical + reserved-inbound while planning a new haul, so
                // concurrent haulers don't all admit against the same stale physical count and overshoot the
                // cap; physical for every other (recheck) caller. No-op when reservation counting is off.
                int n = planning ? data.GetEffectiveCount(t.def, unit) : data.GetCount(t.def, unit);

                // Upper — the maximum (M2 makes this a hard cap at drop time via HardCap_Patches).
                if (lim.Upper.HasValue && n >= lim.Upper.Value)
                {
                    reason = "overCap";
                    if (PscLog.Enabled) LogReject(t, unit, reason,
                        $"limit: rejected {t.def.defName} -> {unit.UniqueLoadID} (at/over cap {n}/{lim.Upper.Value})");
                    return true;
                }

                // Lower / hysteresis (D15): lower unset => always refill; otherwise require refill state.
                if (lim.Lower.HasValue && !data.IsRefilling(t.def))
                {
                    reason = "hysteresis";
                    if (PscLog.Enabled) LogReject(t, unit, reason,
                        $"limit: rejected {t.def.defName} -> {unit.UniqueLoadID} (not refilling; above lower threshold {lim.Lower.Value})");
                    return true;
                }
            }

            // Batch (D12): the trip must be able to deliver at least `batch` in one go.
            if (data.batch > 0)
            {
                // Source-stack gate: never start a trip from a stack smaller than the batch size.
                if (t.stackCount < data.batch)
                {
                    reason = "underBatch";
                    if (PscLog.Enabled) LogReject(t, unit, reason,
                        $"limit: rejected {t.def.defName} -> {unit.UniqueLoadID} (source stack {t.stackCount} < batch {data.batch})");
                    return true;
                }

                // Destination-room gate: a unit that can't fit a full batch (room < batch) is not a valid
                // batch destination. Capped: cap-room arithmetic (effective while planning). Uncapped: gate on
                // vanilla PHYSICAL stack space via the bounded PhysicalRoomForDef scan, but only while
                // planning (an in-flight FailOn recheck must not run the cell scan or self-cancel).
                var blim = hasLimit ? data.GetLimit(t.def) : null;
                if (blim != null && blim.Upper.HasValue)
                {
                    int room = blim.Upper.Value - (planning ? data.GetEffectiveCount(t.def, unit) : data.GetCount(t.def, unit));
                    if (room < data.batch)
                    {
                        reason = "underBatchRoom";
                        if (PscLog.Enabled) LogReject(t, unit, reason,
                            $"limit: rejected {t.def.defName} -> {unit.UniqueLoadID} (cap room < batch {data.batch})");
                        return true;
                    }
                }
                else if (planning && unit.PhysicalRoomForDef(t.def, data.batch) < data.batch)
                {
                    reason = "underBatchRoom";
                    if (PscLog.Enabled) LogReject(t, unit, reason,
                        $"limit: rejected {t.def.defName} -> {unit.UniqueLoadID} (physical room < batch {data.batch})");
                    return true;
                }
            }

            return false;
        }

        // Feeder gates (M3, D11/D16). Evaluated before the target-data early-out: a SOURCE's
        // onlyToDestinations must block hauling its items even into a target with no PSC policy. Both rules
        // reduce to the same functional directed edge (source -> target); loose items have no source edge, so
        // onlyFromSource rejects them. Returns true when the haul must be rejected, and sets targetData if it
        // resolved the target's PSC data (reused by the limit gate). `planning` routes the per-search memos.
        private static bool TryFeederReject(StorageSettings target, Thing t, PscHaulUnit unit,
            PscHaulUnit source, PscStorageData sourceData, ref PscStorageData targetData, bool planning)
        {
            var psc = PscMapComponent.For(unit.Map);
            if (psc == null || !psc.anyFeederActive) return false;

            // Opt B (feeder source short-circuit): a functional edge needs an OUTGOING edge from source; if
            // the source has none, FeederAllows is false for every candidate, so skip the per-candidate
            // lookup. Cached per-search (source is invariant) when planning; fresh otherwise.
            bool hasFunctionalEdge = false;
            if (source.IsValid)
            {
                bool sourceHasDest;
                if (!(planning && PscSearchContext.TryGetSourceHasFeederDest(t, out sourceHasDest)))
                {
                    sourceHasDest = psc.Links.HasAnyDestination(source.UniqueLoadID);
                    if (planning) PscSearchContext.CacheSourceHasFeederDest(t, sourceHasDest);
                }
                // Opt B3: source is invariant within a search, so FeederAllows(source, unit) varies only by
                // target -> memo it per-search keyed by the target unit (cached otherwise).
                if (sourceHasDest)
                {
                    if (!(planning && PscSearchContext.TryGetFeederAllows(t, unit, out hasFunctionalEdge)))
                    {
                        // Cross-search memo (Cache B): memoizes ONLY this FeederAllows(source, target) subquery
                        // across searches; the PscSearchContext memo above is the within-search inner layer.
                        hasFunctionalEdge = psc.FeederDecisions.FeederAllows(psc, source, unit);
                        if (planning) PscSearchContext.CacheFeederAllows(t, unit, hasFunctionalEdge);
                    }
                }
            }
            if (!hasFunctionalEdge && !source.IsValid
                && PscFeederHaulContext.TryGet(t, out var route)
                && route.map == unit.Map
                && route.destId == unit.UniqueLoadID
                && psc.FeederAllows(route.sourceId, route.destId))
            {
                hasFunctionalEdge = true;
            }
            // Loose-item skip (feederSkipLooseItems): a genuinely loose ground item (no source unit AND no
            // active route) may enter a chain with an open mouth and skip straight to this node.
            if (!hasFunctionalEdge && !source.IsValid
                && PscMod.Settings != null && PscMod.Settings.feederSkipHops && PscMod.Settings.feederSkipLooseItems
                && !PscFeederHaulContext.TryGet(t, out _)
                && psc.LooseItemMayEnterChainAt(unit, t))
            {
                hasFunctionalEdge = true;
            }
            if (hasFunctionalEdge) return false;

            if (source.IsValid)
            {
                // A source no longer accepting this item (player disallowed its def, or it's a leftover) must
                // stay evacuable to ANY storage -- onlyToDestinations only holds the source's VALID contents.
                // The nested AllowedToAccept is safe (item's current unit == source, so that postfix early-outs
                // on sourceIsTarget) and short-circuits last.
                if (sourceData != null && sourceData.onlyToDestinations && source.Settings.AllowedToAccept(t))
                {
                    if (PscLog.Enabled) LogReject(t, unit, "onlyToDestinations",
                        $"feeder: rejected {t.def.defName} -> {unit.UniqueLoadID} (source onlyToDestinations, no functional edge)");
                    return true;
                }
            }
            else if (PscFeederHaulContext.TryGet(t, out var carriedRoute) && carriedRoute.map == unit.Map)
            {
                if (PscLog.Enabled) LogReject(t, unit, "carriedRoute",
                    $"feeder: rejected carried {t.def.defName} -> {unit.UniqueLoadID} (planned route no longer a functional edge)");
                return true;
            }

            targetData = PscStorageDataStore.TryGet(target);
            if (targetData != null && targetData.onlyFromSource)
            {
                if (PscLog.Enabled) LogReject(t, unit, "onlyFromSource",
                    $"feeder: rejected {t.def.defName} -> {unit.UniqueLoadID} (target onlyFromSource, no functional edge)");
                return true;
            }
            return false;
        }

        // Throttled dev-log of an admission rejection. Keyed per (def, unit, reason) so a steady haul scan
        // logs each distinct rejection at most once per throttle window.
        private static void LogReject(Thing t, PscHaulUnit unit, string reason, string msg)
        {
            if (PscLog.Enabled) PscLog.MsgThrottled($"adm:{t.def?.defName}:{unit.UniqueLoadID}:{reason}", msg);
        }
    }
}
