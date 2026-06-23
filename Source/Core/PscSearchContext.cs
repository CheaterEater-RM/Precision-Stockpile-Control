using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Per-store-search memo of the haul item's INVARIANT source side (Phase 2 perf).
    //
    // Within one StoreUtility.TryFindBestBetterStoreCellFor — INCLUDING the fine-order rank-primary
    // Postfix re-scan, which drives many AllowedToAccept(t) calls for the same t — the item's current
    // (source) unit, its fine-order rank, and its PscStorageData never change. Resolving them once and
    // sharing across every candidate (and across both ShouldContinueSearch and the admission postfix)
    // removes the dominant repeated work Phase 1 left behind.
    //
    // ThreadStatic to match PscAdmissionScope.InStoreSearch: the admission postfix runs on off-main
    // reachability threads too, so each thread keeps its own context. Set + cleared within a single
    // store search on one sim thread -> multiplayer-deterministic. Cleared in
    // StoreUtility_PlanningScope_Patch.Finalizer.
    //
    // Keyed by ReferenceEquals on the item, so it self-corrects if ever entered with a different t.
    // ShouldContinueSearch (the transpiler callback) is always in-search; the admission postfix reads
    // this ONLY while PscAdmissionScope.InStoreSearch is true (the window the Finalizer brackets) and
    // resolves fresh otherwise (validity rechecks, haul FailOn, UI-ish callers).
    internal static class PscSearchContext
    {
        [System.ThreadStatic] private static Thing item;
        [System.ThreadStatic] private static bool sourceValid;
        [System.ThreadStatic] private static ISlotGroup sourceGroup;
        [System.ThreadStatic] private static int sourceRank;
        [System.ThreadStatic] private static PscStorageData sourceData;
        // Opt B tristate: 0 = unprobed, 1 = source has an outgoing feeder edge, 2 = none.
        [System.ThreadStatic] private static byte feederDestState;

        private static void EnsureFor(Thing t)
        {
            if (ReferenceEquals(t, item)) return;
            item = t;
            feederDestState = 0;
            var cur = PscHaulUnit.ResolveCurrent(t);
            sourceValid = cur.IsValid;
            sourceGroup = cur.group;
            sourceData = sourceValid ? PscStorageDataStore.TryGet(cur.Settings) : null;
            sourceRank = sourceValid ? PscOrder.RankWithinBand(cur.Settings) : 0;
        }

        // The item's source/current unit (canonical). Returns false (and a default unit) when the item
        // is loose / unspawned / carried.
        public static bool TrySource(Thing t, out PscHaulUnit source)
        {
            EnsureFor(t);
            source = sourceValid ? new PscHaulUnit(sourceGroup) : default;
            return sourceValid;
        }

        // Fine-order rank of the source unit (0 when no valid source).
        public static int SourceRank(Thing t) { EnsureFor(t); return sourceRank; }

        // PSC policy of the source unit (null when none / no valid source).
        public static PscStorageData SourceData(Thing t) { EnsureFor(t); return sourceData; }

        // Opt B (feeder source short-circuit): cached "does the source have any outgoing feeder edge?".
        // Returns true if the answer is already known this search; `hasDest` carries it. The caller
        // (TryFeederReject, which holds the map component) computes + CacheSourceHasFeederDest on a miss,
        // keeping this type feeder-agnostic.
        public static bool TryGetSourceHasFeederDest(Thing t, out bool hasDest)
        {
            EnsureFor(t);
            hasDest = feederDestState == 1;
            return feederDestState != 0;
        }

        public static void CacheSourceHasFeederDest(Thing t, bool hasDest)
        {
            EnsureFor(t);
            feederDestState = hasDest ? (byte)1 : (byte)2;
        }

        // Reset every field so no stale state lingers and the strong Thing/group/data refs are dropped
        // between searches. Called from StoreUtility_PlanningScope_Patch.Finalizer.
        public static void Clear()
        {
            item = null;
            sourceValid = false;
            sourceGroup = null;
            sourceRank = 0;
            sourceData = null;
            feederDestState = 0;
        }
    }
}
