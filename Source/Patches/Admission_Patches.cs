using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace PrecisionStockpileControl
{
    // Reservation scope (Regression Fix 2). The admission gate's reservation-aware "effective" count
    // (physical + reserved-inbound) must be read ONLY while planning a NEW haul — i.e. inside the
    // StoreUtility.TryFindBestBetterStoreCellFor store-search. AllowedToAccept is also called by validity
    // re-checks that have nothing to do with planning: JobDriver_HaulToCell's goto/place FailOn via
    // StoreUtility.IsValidStorageFor, StoreUtility.IsInValidStorage / ListerHaulables, and the filter UI.
    // If those read effective, an in-flight hauler's OWN reservation makes its destination read "full" and
    // the hauler self-cancels before delivering (the pile never reaches its cap), and stored items can be
    // flagged misplaced (churn). This flag marks the dynamic extent of the store-search so only the
    // planning gate sees reserved-inbound; everything else reads physical. The hard cap at the carry-drop
    // seam (physical) is the final backstop. ThreadStatic guards off-thread reachability scans; it is
    // multiplayer-deterministic (set and cleared within a single sim-thread call).
    internal static class PscAdmissionScope
    {
        [System.ThreadStatic] public static bool InStoreSearch;

        // The soft-planning count read: effective while planning a new haul, physical for every other
        // AllowedToAccept caller. No-op difference when reservedFillCounting is off (effective == physical).
        public static int PlanningCount(PscStorageData data, ThingDef def, PscHaulUnit unit)
            => InStoreSearch ? data.GetEffectiveCount(def, unit) : data.GetCount(def, unit);
    }

    // Mark the planning store-search so PscAdmissionScope.PlanningCount reads effective only here. A
    // separate patch class from the fine-order transpiler on the same method (FineOrder_Patches.cs) —
    // Harmony composes multiple patch classes on one target. The finalizer runs even if the search throws,
    // and the __state save/restore keeps it re-entrancy-safe.
    [HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor))]
    public static class StoreUtility_PlanningScope_Patch
    {
        public static void Prefix(out bool __state)
        {
            __state = PscAdmissionScope.InStoreSearch;
            PscAdmissionScope.InStoreSearch = true;
        }

        public static void Finalizer(bool __state)
        {
            PscAdmissionScope.InStoreSearch = __state;
        }
    }

    // Stockpile-wide admission gate (design §8). Postfix on the per-group AllowedToAccept(Thing)
    // — the overload TryFindBestBetterStoreCellForWorker calls once per candidate group, NOT the
    // ThingDef overload (which is the scan/UI path; leaving it untouched keeps defs visible in the
    // filter). TIGHTEN-ONLY: never flips a vanilla false to true.
    [HarmonyPatch(typeof(StorageSettings), nameof(StorageSettings.AllowedToAccept), new[] { typeof(Thing) })]
    public static class StorageSettings_AllowedToAccept_Patch
    {
        public static void Postfix(StorageSettings __instance, Thing t, ref bool __result)
        {
            if (!__result) return;                       // never override vanilla rejection
            if (PscStorageDataStore.IsEmpty) return;     // cheapest early-out
            if (t == null) return;

            var unit = PscHaulUnit.ResolveSettings(__instance);
            if (!unit.IsValid) return;

            // Resolve the item's current unit once (loose / unspawned / carried => invalid). An item
            // already stored in THIS unit is validly stored — feeder and limit rules never apply to
            // a unit's own contents (D16), or vanilla's IsInValidBestStorage/Accepts chain would flag
            // a capped or restricted stockpile's own contents as haulable (churn / ejection).
            var source = PscHaulUnit.ResolveCurrent(t);
            bool sourceIsTarget = source.IsValid && source.Equals(unit);
            PscStorageData data = PscStorageDataStore.TryGet(__instance);

            // Mode haul-in block (M5.2): an Off / RetrieveOnly unit accepts no new hauls. Target-keyed
            // and guarded by sourceIsTarget so the unit's own contents are never flagged as misplaced
            // (D16). The freeze side (no haul-out / use) is handled separately by the IsForbidden
            // postfix in StorageMode_Patches.
            if (!sourceIsTarget && data != null &&
                (data.mode == PscStorageMode.Off || data.mode == PscStorageMode.RetrieveOnly))
            {
                LogReject(t, unit, "modeNoIntake",
                    $"mode: rejected {t.def.defName} -> {unit.UniqueLoadID} (mode {data.mode}, no intake)");
                __result = false; return;
            }

            // Feeder gates first (a source's onlyToDestinations blocks the haul even into a no-policy
            // target). targetData is reused by the limit gate when the feeder gate had to resolve it.
            if (!sourceIsTarget && TryFeederReject(__instance, t, unit, source, ref data))
            {
                __result = false; return;
            }

            // Batch empty (source-keyed): never let an item LEAVE its source unless this stack is at least
            // batchEmpty — the most one trip can remove (a haul carries from a single source stack). Mirrors
            // the destination-keyed batch-fill source-stack gate below, and reuses the same source-policy
            // rejection mechanism as the feeder onlyToDestinations rule, so it's churn-safe (only fires when
            // leaving the source; a unit's own contents are protected by sourceIsTarget above).
            if (!sourceIsTarget && source.IsValid)
            {
                var srcData = PscStorageDataStore.TryGet(source.Settings);
                // Same exemption as onlyToDestinations above: never let "no small removals" trap an item
                // the source no longer allows — a disallowed/misplaced item must stay evacuable.
                if (srcData != null && srcData.batchEmpty > 0 && t.stackCount < srcData.batchEmpty
                    && source.Settings.AllowedToAccept(t))
                {
                    LogReject(t, source, "underBatchEmpty",
                        $"batchEmpty: rejected {t.def.defName} leaving {source.UniqueLoadID} (source stack {t.stackCount} < batchEmpty {srcData.batchEmpty})");
                    __result = false; return;
                }
            }

            // --- Limit / batch gates (M1/M2) ---
            data ??= PscStorageDataStore.TryGet(__instance);
            if (data == null) return;
            bool hasLimit = data.HasLimit(t.def);
            if (!hasLimit && data.batch <= 0) return;    // no effective limit and no batch -> vanilla

            // D16: a unit's own contents are normally always valid (never reject/eject them). The ONE
            // documented exception (DESIGN §8): contents STRICTLY over the per-def cap read as misplaced,
            // so vanilla's normal hauling drains the excess to any other valid storage and stops EXACTLY
            // at the cap (== upper is valid again). Covers force-dropped excess, direct spawns, and
            // lowering a limit below current contents. Tighten-only (a vanilla true -> false). The
            // drain-trip clamp in HaulToCellStorageJob keeps a single hauler from overshooting below the
            // cap (anti-oscillation). Reads the cached count, no scan.
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
                            LogReject(t, unit, "overCapDrain",
                                $"limit: draining over-cap {t.def.defName} from {unit.UniqueLoadID} ({ownCount} > cap {ownLim.Upper.Value})");
                            __result = false; return;
                        }
                    }
                }
                return;                                  // own contents otherwise always valid (D16)
            }

            if (hasLimit)
            {
                var lim = data.GetLimit(t.def);
                // Soft planning gate: effective = physical + reserved-inbound, so concurrent haulers
                // don't all admit against the same stale physical count and overshoot the cap. Scoped to
                // the store-search (PscAdmissionScope) so a hauler's own in-flight FailOn re-check reads
                // physical and never self-cancels; the hard drop cap (HardCap_Patches) stays on physical.
                // No-op when the feature is off.
                int n = PscAdmissionScope.PlanningCount(data, t.def, unit);

                // Upper — the maximum (M2 makes this a hard cap at drop time via HardCap_Patches)
                if (lim.Upper.HasValue && n >= lim.Upper.Value)
                {
                    LogReject(t, unit, "overCap",
                        $"limit: rejected {t.def.defName} -> {unit.UniqueLoadID} (at/over cap {n}/{lim.Upper.Value})");
                    __result = false; return;
                }

                // Lower / hysteresis (D15): lower unset => always refill; otherwise require refill state
                if (lim.Lower.HasValue && !data.IsRefilling(t.def))
                {
                    LogReject(t, unit, "hysteresis",
                        $"limit: rejected {t.def.defName} -> {unit.UniqueLoadID} (not refilling; above lower threshold {lim.Lower.Value})");
                    __result = false; return;
                }
            }

            // Batch (D12): the trip must be able to deliver at least `batch` in one go.
            if (data.batch > 0)
            {
                // Source-stack gate: never start a trip from a stack smaller than the batch size.
                if (t.stackCount < data.batch)
                {
                    LogReject(t, unit, "underBatch",
                        $"limit: rejected {t.def.defName} -> {unit.UniqueLoadID} (source stack {t.stackCount} < batch {data.batch})");
                    __result = false; return;
                }

                // Destination-room gate: a capped unit that can't fit a full batch (room < batch) is not
                // a valid batch destination — reject here so vanilla stops treating it as one. Without
                // this, a capped+batched pile sitting in its top <batch window (0 < upper-n < batch)
                // passes admission every haul scan but has the resulting job cancelled by the
                // HaulToCellStorageJob room<batch clamp, churning plan/cancel forever while loose stock
                // exists. The last <batch items are intentionally never topped off ("no small trips");
                // this only stops the wasted re-planning. (n < upper already holds — the over-cap gate
                // above returned otherwise — so room >= 1 here.)
                if (hasLimit)
                {
                    var lim = data.GetLimit(t.def);
                    // Effective room (physical + reserved) so in-flight hauls count toward the batch-room
                    // gate too, but only while planning (store-search scope) — a validity re-check reads physical.
                    if (lim.Upper.HasValue && lim.Upper.Value - PscAdmissionScope.PlanningCount(data, t.def, unit) < data.batch)
                    {
                        LogReject(t, unit, "underBatchRoom",
                            $"limit: rejected {t.def.defName} -> {unit.UniqueLoadID} (room < batch {data.batch})");
                        __result = false; return;
                    }
                }
            }
        }

        // Feeder gates (M3, D11/D16). Evaluated before the target-data early-out: a SOURCE's
        // onlyToDestinations must block hauling its items even into a target with no PSC policy. Both
        // rules reduce to the same functional directed edge (source -> target); loose items have no
        // source edge, so onlyFromSource rejects them. Returns true when the haul must be rejected,
        // and sets targetData if it resolved the target's PSC data (reused by the limit gate).
        private static bool TryFeederReject(StorageSettings target, Thing t, PscHaulUnit unit,
            PscHaulUnit source, ref PscStorageData targetData)
        {
            var psc = PscMapComponent.For(unit.Map);
            if (psc == null || !psc.anyFeederActive) return false;

            bool hasFunctionalEdge = source.IsValid && psc.FeederAllows(source, unit);
            if (!hasFunctionalEdge && !source.IsValid
                && PscFeederHaulContext.TryGet(t, out var route)
                && route.map == unit.Map
                && route.destId == unit.UniqueLoadID
                && psc.FeederAllows(route.sourceId, route.destId))
            {
                hasFunctionalEdge = true;
            }
            // Loose-item skip (feederSkipLooseItems): a genuinely loose ground item (no source unit AND no
            // active route) may enter a chain that has an open mouth and skip straight to this node. The
            // no-route guard keeps an in-flight feeder haul pinned to its destination by the carriedRoute
            // reject below instead of being diverted here.
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
                var srcData = PscStorageDataStore.TryGet(source.Settings);
                // A source no longer accepting this item (player disallowed its def, or it's a leftover)
                // must stay evacuable to ANY storage — onlyToDestinations only holds the source's VALID
                // contents. AllowedToAccept is vanilla's "validly stored here" test; the nested call is
                // safe (the item's current unit == source, so that postfix early-outs on sourceIsTarget)
                // and short-circuits last, so it only runs when this gate would otherwise reject.
                if (srcData != null && srcData.onlyToDestinations && source.Settings.AllowedToAccept(t))
                {
                    LogReject(t, unit, "onlyToDestinations",
                        $"feeder: rejected {t.def.defName} -> {unit.UniqueLoadID} (source onlyToDestinations, no functional edge)");
                    return true;
                }
            }
            else if (PscFeederHaulContext.TryGet(t, out var carriedRoute) && carriedRoute.map == unit.Map)
            {
                LogReject(t, unit, "carriedRoute",
                    $"feeder: rejected carried {t.def.defName} -> {unit.UniqueLoadID} (planned route no longer a functional edge)");
                return true;
            }

            targetData = PscStorageDataStore.TryGet(target);
            if (targetData != null && targetData.onlyFromSource)
            {
                LogReject(t, unit, "onlyFromSource",
                    $"feeder: rejected {t.def.defName} -> {unit.UniqueLoadID} (target onlyFromSource, no functional edge)");
                return true;
            }
            return false;
        }

        // Throttled dev-log of an admission rejection. Keyed per (def, unit, reason) so a steady haul
        // scan logs each distinct rejection at most once per throttle window.
        private static void LogReject(Thing t, PscHaulUnit unit, string reason, string msg)
        {
            if (PscLog.Enabled) PscLog.MsgThrottled($"adm:{t.def?.defName}:{unit.UniqueLoadID}:{reason}", msg);
        }
    }

    // Haul-count upper clamp (design §7, §8). Postfix on the job builder: cap job.count so the trip
    // never plans to exceed the target maximum. Vanilla's own clamp point is
    // job.count = Mathf.Min(job.count, num); this runs after it.
    [HarmonyPatch(typeof(HaulAIUtility), nameof(HaulAIUtility.HaulToCellStorageJob),
        new[] { typeof(Pawn), typeof(Thing), typeof(IntVec3), typeof(bool) })]
    public static class HaulAIUtility_HaulToCellStorageJob_Patch
    {
        public static void Postfix(Pawn p, Thing t, IntVec3 storeCell, ref Job __result)
        {
            if (__result == null) return;
            if (PscStorageDataStore.IsEmpty) return;
            if (t == null || p?.Map == null) return;

            var slot = p.Map.haulDestinationManager.SlotGroupAt(storeCell);
            if (slot == null) return;
            ISlotGroup canon = slot.StorageGroup != null ? (ISlotGroup)slot.StorageGroup : slot;
            var targetUnit = new PscHaulUnit(canon);
            var sourceUnit = PscHaulUnit.ResolveCurrent(t);
            if (t.Spawned && t.Position == storeCell) { __result = null; PscFeederHaulContext.Clear(t); return; }
            if (sourceUnit.IsValid && sourceUnit.Equals(targetUnit)) { __result = null; PscFeederHaulContext.Clear(t); return; }

            var psc = PscMapComponent.For(p.Map);
            if (psc != null && sourceUnit.IsValid && psc.FeederAllows(sourceUnit, targetUnit))
            {
                __result.haulOpportunisticDuplicates = false;
                PscFeederHaulContext.Register(t, p.Map, sourceUnit.UniqueLoadID, targetUnit.UniqueLoadID);
            }
            else
            {
                PscFeederHaulContext.Clear(t);
            }

            var data = PscStorageDataStore.TryGet(canon.Settings);
            var sourceData = sourceUnit.IsValid ? PscStorageDataStore.TryGet(sourceUnit.Settings) : null;
            bool sourceHasBatchEmpty = sourceData != null && sourceData.batchEmpty > 0;
            bool sourceHasLimit = sourceData != null && sourceData.HasLimit(t.def);
            // The target-keyed clamps need a limit or fill-batch; the source-keyed batch-empty cancel and
            // the source-over-cap drain clamp below must run even when the target has no PSC policy, so
            // they can't share a single early-out.
            bool targetHasClampPolicy = data != null && (data.HasLimit(t.def) || data.batch > 0);
            if (!targetHasClampPolicy && !sourceHasBatchEmpty && !sourceHasLimit) return;

            if (data != null)
            {
                // Upper clamp — cap the planned count so the trip never plans past the maximum.
                if (data.HasLimit(t.def))
                {
                    var lim = data.GetLimit(t.def);
                    if (lim.Upper.HasValue)
                    {
                        // Effective room (physical + reserved-inbound). The current job is NOT yet
                        // registered (that happens after all clamps below), so this excludes its own
                        // reservation -> automatic self-exclusion. No-op when the feature is off.
                        int room = Math.Max(0, lim.Upper.Value - data.GetEffectiveCount(t.def, targetUnit));
                        if (room <= 0)
                        {
                            // No room (reservation-overshoot window between admission and job build):
                            // cancel rather than emit a count-0 job, which trips vanilla's
                            // Toils_Haul.ErrorCheckForCarry "Invalid count: 0" Log.Error + 1-item trip.
                            if (PscLog.Enabled) PscLog.Msg(
                                $"limit: cancelled haul of {t.def.defName} -> {targetUnit.UniqueLoadID} (at/over cap, no room)");
                            PscFeederHaulContext.Clear(t);
                            __result = null;
                            return;
                        }
                        if (__result.count > room)
                        {
                            if (PscLog.Enabled) PscLog.Msg(
                                $"limit: clamped haul of {t.def.defName} -> {targetUnit.UniqueLoadID} from {__result.count} to {room} (cap room)");
                            __result.count = room;
                        }
                    }
                }

                // Batch fill (D12): never bring fewer than `batch` in one trip — cancel an under-batch job.
                // Note: when remaining room < batch (a near-full capped unit), the last < batch items are
                // intentionally not hauled ("no small trips"). Opportunistic duplicates are left as vanilla.
                if (data.batch > 0 && __result.count < data.batch)
                {
                    if (PscLog.Enabled) PscLog.Msg(
                        $"limit: cancelled under-batch haul of {t.def.defName} -> {targetUnit.UniqueLoadID} ({__result.count} < batch {data.batch})");
                    PscFeederHaulContext.Clear(t);
                    __result = null;
                }
            }

            // Source-over-cap excess (DESIGN §8 anti-oscillation). When the source unit holds STRICTLY
            // more than its cap for this def, the AllowedToAccept carve-out has made the excess read as
            // misplaced and this trip is draining it. Compute the excess once; reads the cached count, no
            // scan. srcExcess <= 0 means the source is within cap (a normal haul-out), so neither the
            // drain clamp nor the batch-empty exemption below engages.
            int srcExcess = 0;
            if (sourceHasLimit)
            {
                var srcLim = sourceData.GetLimit(t.def);
                if (srcLim.Upper.HasValue)
                    srcExcess = Math.Max(0, sourceData.GetCount(t.def, sourceUnit) - srcLim.Upper.Value);
            }

            // Drain clamp: clamp an over-cap drain to exactly the excess so a single hauler lands the
            // source on its cap rather than overshooting below it (which could otherwise re-trigger refill
            // and ping-pong). Mirrors the target room clamp above.
            if (__result != null && srcExcess > 0 && __result.count > srcExcess)
            {
                if (PscLog.Enabled) PscLog.Msg(
                    $"limit: clamped over-cap drain of {t.def.defName} from {sourceUnit.UniqueLoadID} to {srcExcess} (excess over cap)");
                __result.count = srcExcess;
            }

            // Batch empty (source-keyed): the trip must take at least batchEmpty OUT of the source — cancel an
            // under-batch removal. Runs last so it sees the final clamped count (vanilla room + carry capacity
            // + the upper clamp above). Best-effort under inventory-haul mods (PUAH / Hauler's Dream) which
            // don't build their bulk jobs through HaulToCellStorageJob; the admission gate is the line there.
            // Exempt an over-cap DRAIN (srcExcess > 0): evacuating excess the player capped out takes
            // priority over "no small removal trips", so a sub-batchEmpty excess is never trapped.
            if (__result != null && sourceHasBatchEmpty && srcExcess <= 0 && __result.count < sourceData.batchEmpty)
            {
                if (PscLog.Enabled) PscLog.Msg(
                    $"batchEmpty: cancelled under-batch removal of {t.def.defName} from {sourceUnit.UniqueLoadID} ({__result.count} < batchEmpty {sourceData.batchEmpty})");
                PscFeederHaulContext.Clear(t);
                __result = null;
            }

            // Reserved-inbound INCREMENT is deliberately NOT done here. This builder
            // (HaulAIUtility.HaulToCellStorageJob) also runs during feasibility probes:
            // WorkGiver_Scanner.HasJobOnThing == JobOnThing(...) != null builds and discards a full job,
            // so reserving here charged a phantom +count on every scan with no matching delivery decrement
            // (the job never starts). That pinned a capped pile's effective count at the cap (all hauling
            // blocked) and desynced HasJobOnThing from the second JobOnThing (the "yielded no actual job"
            // error). The increment now lives at the genuine commitment seam,
            // JobDriver_HaulToCell.Notify_Starting (CountCache_Patches.cs), reached only when a job really
            // starts executing. The effective-room READ above stays — it has no persistent side effect.
        }
    }
}
