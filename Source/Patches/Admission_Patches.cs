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
            var data = PscStorageDataStore.TryGet(__instance);
            if (data == null) return;
            bool hasLimit = data.HasLimit(t.def);
            if (!hasLimit && data.batch <= 0) return;    // no per-def limit and no batch -> vanilla

            var unit = PscHaulUnit.ResolveSettings(__instance);
            if (!unit.IsValid) return;

            // An item already stored in THIS unit is validly stored — never reject it, or vanilla's
            // IsInValidBestStorage/Accepts chain would flag a capped stockpile's own contents as
            // haulable (churn / unwanted ejection). PSC rules gate genuinely incoming items only.
            // This is the D16 source==target guard, applied to the upper/lower AND batch rules.
            if (PscHaulUnit.ResolveCurrent(t).Equals(unit)) return;

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

            // --- M3 slot: feeder onlyFromSource / onlyToDestinations, guarded by D16 source==target ---
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
