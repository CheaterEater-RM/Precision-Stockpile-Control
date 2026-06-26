using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Per-store-search memo of the haul item's INVARIANT source side (store-search rewrite, Phase 2).
    //
    // Within one StoreUtility.TryFindBestBetterStoreCellFor the item's current (source) unit and its
    // PscStorageData never change, and FeederAllows(source, target) varies only by target. The
    // PscStoreSearchEngine resolves the source once and reuses it across every candidate it ranks (and the
    // Hauler's Dream re-validation postfix reads the same memo for that item, via HardReject(planning: true)),
    // removing repeated per-candidate source resolution and feeder lookups. The AllowedToAccept backstop no
    // longer uses this: after the rewrite it serves only external callers and resolves fresh.
    //
    // ThreadStatic: DEFENSIVE per-thread isolation for the case the engine is ever reached off-main by a
    // threading caller (RimWorld Multiplayer's sim thread, or a threading mod). Vanilla 1.6 runs the store
    // search synchronously on the MAIN thread (WorkGiver_Haul -> HaulAIUtility -> StoreUtility; reachability is
    // main-thread too), so this is not a vanilla requirement, and the defense is only partial -- the live count
    // model (PscStorageData.counts / reservedInbound) is NOT concurrency-safe. See
    // STORE_SEARCH_REWRITE_PHASE4_DESIGN.md §6.1. Set + cleared within a single store search on one thread ->
    // multiplayer-deterministic. Cleared in
    // StoreUtility_Engine_Patch.Finalizer, which runs after the whole postfix chain so the memo survives
    // through the HD re-validation postfix.
    //
    // Keyed by ReferenceEquals on the item, so it self-corrects if ever entered with a different t.
    internal static class PscSearchContext
    {
        [System.ThreadStatic] private static Thing item;
        [System.ThreadStatic] private static bool sourceValid;
        [System.ThreadStatic] private static ISlotGroup sourceGroup;
        [System.ThreadStatic] private static PscStorageData sourceData;
        // Opt B tristate: 0 = unprobed, 1 = source has an outgoing feeder edge, 2 = none.
        [System.ThreadStatic] private static byte feederDestState;

        // Opt B3 feeder-decision MRU (single entry, keyed by the target unit's group). Within one search the
        // source is invariant, so FeederAllows(source, target) varies only by target; cache the last
        // (target.group -> allows) result. Reset on item change in EnsureFor (a new item = a new source) and
        // per search in Clear.
        [System.ThreadStatic] private static ISlotGroup feederTargetGroup;
        [System.ThreadStatic] private static bool feederTargetAllows;

        private static void EnsureFor(Thing t)
        {
            if (ReferenceEquals(t, item)) return;
            item = t;
            feederDestState = 0;
            feederTargetGroup = null;
            var cur = PscHaulUnit.ResolveCurrent(t);
            sourceValid = cur.IsValid;
            sourceGroup = cur.group;
            sourceData = sourceValid ? PscStorageDataStore.TryGet(cur.Settings) : null;
        }

        // The item's source/current unit (canonical). Returns false (and a default unit) when the item is
        // loose / unspawned / carried.
        public static bool TrySource(Thing t, out PscHaulUnit source)
        {
            EnsureFor(t);
            source = sourceValid ? new PscHaulUnit(sourceGroup) : default;
            return sourceValid;
        }

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

        // Opt B3 (feeder decision memo): cached "does FeederAllows(source, target) hold?" for the current
        // search's invariant source. Keyed by the target unit's group; returns true if the answer is already
        // known this search (`allows` carries it). EnsureFor self-resets the entry when the item (hence
        // source) changes. TryFeederReject (which holds the map component) computes + CacheFeederAllows on a
        // miss, keeping this type feeder-agnostic.
        public static bool TryGetFeederAllows(Thing t, PscHaulUnit target, out bool allows)
        {
            EnsureFor(t);
            allows = feederTargetAllows;
            return target.group != null && ReferenceEquals(target.group, feederTargetGroup);
        }

        public static void CacheFeederAllows(Thing t, PscHaulUnit target, bool allows)
        {
            EnsureFor(t);
            feederTargetGroup = target.group;
            feederTargetAllows = allows;
        }

        // Reset every field so no stale state lingers and the strong Thing/group/data refs are dropped between
        // searches. Called from StoreUtility_Engine_Patch.Finalizer.
        public static void Clear()
        {
            item = null;
            sourceValid = false;
            sourceGroup = null;
            sourceData = null;
            feederDestState = 0;
            feederTargetGroup = null;
            feederTargetAllows = false;
        }
    }
}
