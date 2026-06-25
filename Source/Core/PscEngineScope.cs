using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Thread-static scopes for the store-search engine (store-search rewrite, Phase 2).
    //
    // TWO ORTHOGONAL MECHANISMS, never conflated (the Codex round-3 blocking correction):
    //   - BypassAdmissionBackstop is PURELY the admission bypass. The engine sets it in a try/finally around
    //     ONLY the delegated StoreUtility.TryFindBestBetterStoreCellForIn cell probe, so the retained
    //     StorageSettings.AllowedToAccept backstop early-outs there (admission was already decided by the
    //     engine's own hard-admit). It is NEVER set for the whole prefix and NEVER selects a count or routes a
    //     per-search memo. The effective-vs-physical count and the PscSearchContext memo are chosen by the
    //     `planning` parameter on PscAdmissionIndex.HardReject (engine -> true, backstop -> false), a SEPARATE
    //     mechanism. (If the bypass flag also carried the count, the engine's hard-admit -- which runs BEFORE
    //     the cell probe -- would read physical and lose reserved-inbound; widening the flag to cover
    //     hard-admit would re-introduce the nested-source bypass bug. Hence the split.)
    //   - ExcludedCells is the cell-exclusion set for the bulk adapters (PUAH skipCells, Phase 4); the
    //     IsGoodStoreCell gate reads it, set in the same window as the bypass. null / empty on the vanilla path.
    //   - IntendedUnitGroup is the canonical group the engine selected for this search. The HD re-validation
    //     postfix reads it (after HD's haul-to-stack postfix may have re-pointed the cell into a different
    //     same-priority unit) to confirm "PSC owns which unit"; the engine prefix's Finalizer clears it.
    //   - VanillaFallbackPlanning marks a search the engine CEDED to vanilla/LWM (an LWM Deep Storage source
    //     item, PscStoreSearchEngine's DSU decline) while PSC policy is still active. On that path the engine
    //     does no hard-admit, so the retained AllowedToAccept backstop is the planning gate -- and a store
    //     search is planning, so it must read effective/reserved counts (HardReject planning: true) or
    //     concurrent DSU relocations all admit into the same capped unit against a stale physical count and
    //     overshoot. Set ONLY at the DSU cede (so it never affects the in-flight FailOn recheck path, which
    //     correctly reads physical), cleared by the engine prefix's Finalizer. This is distinct from
    //     BypassAdmissionBackstop (which SILENCES the backstop) and from HardReject's own `planning` parameter
    //     (which it merely supplies a value for on this one ceded path).
    //
    // ThreadStatic so off-main reachability scans keep their own state; set and cleared within one search on
    // one sim thread -> multiplayer-deterministic.
    internal static class PscEngineScope
    {
        [System.ThreadStatic] public static bool BypassAdmissionBackstop;
        [System.ThreadStatic] public static HashSet<IntVec3> ExcludedCells;
        [System.ThreadStatic] public static ISlotGroup IntendedUnitGroup;
        [System.ThreadStatic] public static bool VanillaFallbackPlanning;
    }
}
