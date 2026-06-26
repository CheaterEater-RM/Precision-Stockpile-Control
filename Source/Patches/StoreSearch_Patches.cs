using HarmonyLib;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // The store-search engine cutover (store-search rewrite, Phase 2). A prefix on the outer
    // StoreUtility.TryFindBestBetterStoreCellFor hands the whole slot-group planning result to
    // PscStoreSearchEngine; the engine ranks all eligible groups and delegates cell legality to vanilla
    // …ForIn. This replaces the deleted fine-order transpiler + rank-primary re-scan + InStoreSearch
    // planning scope. Plus the excluded-cell gate (Phase 4 bulk readiness) and the Hauler's Dream
    // re-validation postfix (keeps "PSC owns which unit" against HD's haul-to-stack postfix).

    [HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor))]
    [HarmonyPriority(Priority.Low)]   // run AFTER PSC's per-tile relocate prefix (unprioritized => Normal) so
                                      // its by-ref currentPriority write lands first. This is the only
                                      // ordering need; it need not be globally last among all mods.
    public static class StoreUtility_Engine_Patch
    {
        public static bool Prefix(Thing t, Pawn carrier, Map map, StoragePriority currentPriority,
            Faction faction, ref IntVec3 foundCell, bool needAccurateResult, ref bool __result)
        {
            PscEngineScope.IntendedUnitGroup = null;        // clear stale before the call; set only on Found
            PscEngineScope.VanillaFallbackPlanning = false;  // set only when the engine cedes a DSU-resident item
            var res = PscStoreSearchEngine.TryFindBestStoreCell(t, carrier, map, currentPriority, faction,
                PscSearchOptions.Default(needAccurateResult), out var cell);
            if (res == PscStoreSearchEngine.PscSearchResult.Unaffected) return true;   // run vanilla
            foundCell = res == PscStoreSearchEngine.PscSearchResult.Found ? cell : IntVec3.Invalid;
            __result = res == PscStoreSearchEngine.PscSearchResult.Found;
            return false;                                                             // suppress vanilla body
        }

        // Runs after the WHOLE postfix chain (including HD's refinement and the re-validation postfix below),
        // so it is the single place the per-search memo + engine scopes are cleared. Replaces the deleted
        // StoreUtility_PlanningScope_Patch.Finalizer's PscSearchContext.Clear().
        public static void Finalizer()
        {
            PscEngineScope.IntendedUnitGroup = null;
            PscEngineScope.ExcludedCells = null;
            PscEngineScope.BypassAdmissionBackstop = false;
            PscEngineScope.VanillaFallbackPlanning = false;
            PscStoreSearchEngine.ClearThreadStaticState();   // release the engine's candidate buffer refs
            PscSearchContext.Clear();
        }
    }

    // The excluded-cell gate (a tighten-only postfix on IsGoodStoreCell that drops cells in
    // PscEngineScope.ExcludedCells) is DEFERRED to Phase 4. IsGoodStoreCell is a hot per-cell method, and in
    // Phase 2 ExcludedCells is always null (only the Phase 4 PUAH extra-item adapter supplies it), so wiring
    // the postfix now would put a no-op dispatch on every cell scan -- exactly the overhead this rewrite
    // removes. The engine already forwards options.ExcludedCells into the scope, so Phase 4 adds only the
    // postfix.

    // Hauler's Dream re-validation (design §4.9). A prefix returning false does NOT skip postfixes, so HD's
    // haul-to-stack postfix on this same method still runs on PSC's result and CAN re-point foundCell into a
    // DIFFERENT same-priority unit than the engine chose (verified against HD source). That would break
    // "PSC owns which unit". Ordered after HD's postfix; when PSC owned the search and the final cell's unit
    // differs from the intended unit, re-validate against PSC policy and veto if the final unit ranks WORSE by
    // PSC's fine-order full key (HD's stack search is fine-order-blind, so it can demote into a lower sub-tier/
    // letter unit that HardReject would not catch) OR violates a hard rule (cap/feeder/mode).
    // This is re-validation, not re-selection: a same-unit cell refinement, and a cross-unit move to an
    // equal-full-key unit (a legitimate stack-merge), both pass untouched.
    [HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor))]
    [HarmonyAfter("giwaffed.HaulersDream")]
    [HarmonyPriority(Priority.VeryLow)]
    public static class StoreUtility_Engine_Revalidate_Patch
    {
        public static void Postfix(Thing t, Map map, ref IntVec3 foundCell, ref bool __result)
        {
            if (!__result) return;
            var intended = PscEngineScope.IntendedUnitGroup;   // null => PSC did not own this search
            if (intended == null || t == null || map == null) return;
            var finalUnit = PscHaulUnit.ResolveCell(foundCell, map);
            if (!finalUnit.IsValid || ReferenceEquals(finalUnit.group, intended)) return;   // same unit: only a cell refine

            // HD re-pointed the cell into a DIFFERENT canonical unit than the engine chose. Veto if that move
            // breaks "PSC owns which unit" in either of two independent ways. (1) the final unit ranks strictly
            // WORSE by PSC's full key (band, then within-band fine-order rank): HD's haul-to-stack search is
            // fine-order-blind, so it can demote the item into a lower sub-tier / letter unit holding a partial
            // stack, and HardReject would NOT catch that (the lower unit may break no cap/feeder/mode rule). A
            // same-full-key unit (two undifferentiated units in one fine-order bucket) is a legitimate stack-merge
            // and is left alone -- the player expressed no fine-order preference between them. (2) the final unit
            // HARD-rejects the item (cap / feeder / mode) -- the engine would never have picked it.
            bool worseFineOrder = PscOrder.Outranks(intended.Settings, finalUnit.Settings);

            var source = PscHaulUnit.ResolveCurrent(t);
            var sourceData = source.IsValid ? PscStorageDataStore.TryGet(source.Settings) : null;
            bool sourceIsTarget = source.IsValid && source.Equals(finalUnit);
            // planning: true -- re-pointing the haul into a different unit is still a planning decision, so
            // effective / reserved counts apply (the engine populated the per-search memo for t this search).
            string reason = null;
            if (worseFineOrder
                || PscAdmissionIndex.HardReject(finalUnit.Settings, t, finalUnit, source, sourceData,
                    sourceIsTarget, planning: true, out reason))
            {
                if (PscLog.Enabled)
                    PscLog.MsgThrottled($"engveto:{t.def?.defName}:{finalUnit.UniqueLoadID}",
                        $"engine: vetoed cross-unit refine {t.def?.defName} -> {finalUnit.UniqueLoadID} "
                        + (worseFineOrder ? "(worse fine-order key)" : $"(reason {reason})"));
                foundCell = IntVec3.Invalid;
                __result = false;
            }
        }
    }
}
