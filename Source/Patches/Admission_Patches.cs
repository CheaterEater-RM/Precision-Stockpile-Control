using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace PrecisionStockpileControl
{
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
                if (srcData != null && srcData.batchEmpty > 0 && t.stackCount < srcData.batchEmpty)
                {
                    LogReject(t, source, "underBatchEmpty",
                        $"batchEmpty: rejected {t.def.defName} leaving {source.UniqueLoadID} (source stack {t.stackCount} < batchEmpty {srcData.batchEmpty})");
                    __result = false; return;
                }
            }

            // --- Limit / batch gates (M1/M2) ---
            data ??= PscStorageDataStore.TryGet(__instance);
            if (data == null) return;
            bool hasLimit = data.HasEffectiveLimit(t.def);
            if (!hasLimit && data.batch <= 0) return;    // no effective limit and no batch -> vanilla
            if (sourceIsTarget) return;                  // D16: never reject a unit's own contents

            if (hasLimit)
            {
                var lim = data.GetEffectiveLimit(t.def);
                int n = data.GetCount(t.def, unit);

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

            // Batch (D12): never start a trip with a source stack smaller than the batch size.
            if (data.batch > 0 && t.stackCount < data.batch)
            {
                LogReject(t, unit, "underBatch",
                    $"limit: rejected {t.def.defName} -> {unit.UniqueLoadID} (source stack {t.stackCount} < batch {data.batch})");
                __result = false; return;
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

            bool hasFunctionalEdge = source.IsValid && psc.HasFunctionalFeederEdge(source, unit);
            if (!hasFunctionalEdge && !source.IsValid
                && PscFeederHaulContext.TryGet(t, out var route)
                && route.map == unit.Map
                && route.destId == unit.UniqueLoadID
                && psc.HasFunctionalFeederEdge(route.sourceId, route.destId))
            {
                hasFunctionalEdge = true;
            }
            if (hasFunctionalEdge) return false;

            if (source.IsValid)
            {
                var srcData = PscStorageDataStore.TryGet(source.Settings);
                if (srcData != null && srcData.onlyToDestinations)
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
            if (psc != null && sourceUnit.IsValid && psc.HasFunctionalFeederEdge(sourceUnit, targetUnit))
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
            // The target-keyed clamps need a limit or fill-batch; the source-keyed batch-empty cancel below
            // must run even when the target has no PSC policy, so it can't share a single early-out.
            bool targetHasClampPolicy = data != null && (data.HasEffectiveLimit(t.def) || data.batch > 0);
            if (!targetHasClampPolicy && !sourceHasBatchEmpty) return;

            if (data != null)
            {
                // Upper clamp — cap the planned count so the trip never plans past the maximum.
                if (data.HasEffectiveLimit(t.def))
                {
                    var lim = data.GetEffectiveLimit(t.def);
                    if (lim.Upper.HasValue)
                    {
                        int room = Math.Max(0, lim.Upper.Value - data.GetCount(t.def, targetUnit));
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

            // Batch empty (source-keyed): the trip must take at least batchEmpty OUT of the source — cancel an
            // under-batch removal. Runs last so it sees the final clamped count (vanilla room + carry capacity
            // + the upper clamp above). Best-effort under inventory-haul mods (PUAH / Hauler's Dream) which
            // don't build their bulk jobs through HaulToCellStorageJob; the admission gate is the line there.
            if (__result != null && sourceHasBatchEmpty && __result.count < sourceData.batchEmpty)
            {
                if (PscLog.Enabled) PscLog.Msg(
                    $"batchEmpty: cancelled under-batch removal of {t.def.defName} from {sourceUnit.UniqueLoadID} ({__result.count} < batchEmpty {sourceData.batchEmpty})");
                PscFeederHaulContext.Clear(t);
                __result = null;
            }
        }
    }
}
