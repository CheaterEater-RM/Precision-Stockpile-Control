using HarmonyLib;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // M4 fine-order sort tiebreak (design §7, §9). A postfix on
    // HaulDestinationManager.CompareSlotGroupPrioritiesDescending breaks the sorted group list by PSC
    // fine-order within a vanilla band, so newly-hauled / unstored items prefer the finer-ranked group.
    // It never touches IL and composes with LWM Deep Storage (PSC only changes which GROUPS the search
    // considers; each candidate cell still flows through IsGoodStoreCell / NoStorageBlockersIn).
    //
    // The fine-order continuation transpiler and the rank-primary re-scan postfix that used to live here
    // were deleted in the Phase 2 store-search rewrite: PscStoreSearchEngine owns candidate order now. This
    // sort tiebreak is retained as legacy / PSC-unaffected support -- the engine computes rank explicitly and
    // does not trust AllGroupsListInPriorityOrder ordering as its source of truth.

    [HarmonyPatch(typeof(HaulDestinationManager), "CompareSlotGroupPrioritiesDescending")]
    public static class HaulDestinationManager_Compare_Patch
    {
        // Vanilla returns ((int)b.Priority).CompareTo((int)a.Priority) (descending). When two groups
        // share a band (__result == 0), break the tie by fine-order: lower rank (better) sorts first.
        public static void Postfix(ref int __result, SlotGroup a, SlotGroup b)
        {
            if (__result != 0) return;
            if (PscStorageDataStore.IsEmpty) return;
            var sa = a?.Settings;
            var sb = b?.Settings;
            if (sa == null || sb == null) return;
            // Only a non-default fine-order key (sub-tier / letter) can break a same-band tie. With none
            // active on this map, CompareWithinBand is always 0 (every same-band unit ranks equal), so skip
            // the per-pair rank work entirely in limits/batch/alarm-only colonies. For() is memoised, so the
            // gate itself is cheap.
            var psc = PscMapComponent.For(a.parent?.Map);
            if (psc == null || !psc.anyFineOrderActive) return;
            __result = PscOrder.CompareWithinBand(sa, sb);
            if (__result != 0 && PscLog.Enabled)
            {
                string aId = PscHaulUnit.ResolveSettings(sa).UniqueLoadID;
                string bId = PscHaulUnit.ResolveSettings(sb).UniqueLoadID;
                PscLog.MsgThrottled($"cmp:{aId}:{bId}",
                    $"order: same-band tiebreak {aId} vs {bId} -> {(__result < 0 ? "a first" : "b first")}");
            }
        }
    }
}
