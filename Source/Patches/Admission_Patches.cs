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

            // --- Feeder gates (M3, D11/D16). Evaluated before the target-data early-out: a SOURCE's
            // onlyToDestinations must block hauling its items even into a target with no PSC policy.
            // Both rules reduce to the same directed edge (source -> target). ---
            if (source.IsValid && !sourceIsTarget)
            {
                var psc = PscMapComponent.For(unit.Map);
                if (psc != null && psc.anyFeederActive)
                {
                    string dstId = unit.UniqueLoadID;
                    string srcId = source.UniqueLoadID;
                    if (!psc.Links.HasEdge(srcId, dstId))
                    {
                        var srcData = PscStorageDataStore.TryGet(source.Settings);
                        if (srcData != null && srcData.onlyToDestinations) { __result = false; return; }
                        var tgtData = PscStorageDataStore.TryGet(__instance);
                        if (tgtData != null && tgtData.onlyFromSource) { __result = false; return; }
                    }
                }
            }

            // --- Limit / batch gates (M1/M2) ---
            var data = PscStorageDataStore.TryGet(__instance);
            if (data == null) return;
            bool hasLimit = data.HasLimit(t.def);
            if (!hasLimit && data.batch <= 0) return;    // no per-def limit and no batch -> vanilla
            if (sourceIsTarget) return;                  // D16: never reject a unit's own contents

            if (hasLimit)
            {
                var lim = data.GetLimit(t.def);
                int n = data.GetCount(t.def, unit);

                // Upper — the maximum (M2 makes this a hard cap at drop time via HardCap_Patches)
                if (lim.Upper.HasValue && n >= lim.Upper.Value) { __result = false; return; }

                // Lower / hysteresis (D15): lower unset => always refill; otherwise require refill state
                if (lim.Lower.HasValue && !data.IsRefilling(t.def)) { __result = false; return; }
            }

            // Batch (D12): never start a trip with a source stack smaller than the batch size.
            if (data.batch > 0 && t.stackCount < data.batch) { __result = false; return; }
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

            var data = PscStorageDataStore.TryGet(canon.Settings);
            if (data == null) return;
            if (!data.HasLimit(t.def) && data.batch <= 0) return;

            // Upper clamp — cap the planned count so the trip never plans past the maximum.
            if (data.HasLimit(t.def))
            {
                var lim = data.GetLimit(t.def);
                if (lim.Upper.HasValue)
                {
                    var unit = new PscHaulUnit(canon);
                    int room = Math.Max(0, lim.Upper.Value - data.GetCount(t.def, unit));
                    if (__result.count > room) __result.count = room;
                }
            }

            // Batch (D12): never bring fewer than `batch` in one trip — cancel an under-batch job.
            // Note: when remaining room < batch (a near-full capped unit), the last < batch items are
            // intentionally not hauled ("no small trips"). Opportunistic duplicates are left as vanilla.
            if (data.batch > 0 && __result.count < data.batch) __result = null;
        }
    }
}
