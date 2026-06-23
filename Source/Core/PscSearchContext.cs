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

        // Opt A3 target-side resolution MRU (single entry, reference-keyed by the candidate
        // StorageSettings). The fine-order re-scan probes the same target group per cell, so caching
        // the last StorageSettings -> unit resolution removes the repeated ResolveSettings work. Used
        // ONLY in-search (the postfix gates on PscAdmissionScope.InStoreSearch); canonical resolution
        // can shift around link/unlink/despawn, so the entry must not outlive the search. Caches the
        // invalid/default result too (targetGroup == null) so ownerless settings don't rebuild. Item-
        // agnostic (keyed by settings), so NOT reset on item change — only per search in Clear.
        [System.ThreadStatic] private static StorageSettings targetSettings;
        [System.ThreadStatic] private static ISlotGroup targetGroup;

        // Opt B3 feeder-decision MRU (single entry, keyed by the target unit's group). Within one
        // search the source is invariant, so FeederAllows(source, target) varies only by target; cache
        // the last (target.group -> allows) result. Source-scoped: reset on item change in EnsureFor
        // (a new item = a new source) and per search in Clear.
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

        // Opt A3: the candidate/target unit for `settings`, MRU-cached by reference identity. Caches
        // the invalid/default unit too (ownerless settings -> group == null). Call only while in-search
        // (the postfix gates this on PscAdmissionScope.InStoreSearch); Clear() drops the entry per
        // search so a stale group never outlives a link/unlink/despawn.
        public static PscHaulUnit TargetUnit(StorageSettings settings)
        {
            if (ReferenceEquals(settings, targetSettings)) return new PscHaulUnit(targetGroup);
            var u = PscHaulUnit.ResolveSettings(settings);
            targetSettings = settings;
            targetGroup = u.group;
            return u;
        }

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

        // Opt B3 (feeder decision memo): cached "does FeederAllows(source, target) hold?" for the
        // current search's invariant source. Keyed by the target unit's group; returns true if the
        // answer is already known this search (`allows` carries it). EnsureFor self-resets the entry
        // when the item (hence source) changes. TryFeederReject (which holds the map component)
        // computes + CacheFeederAllows on a miss, keeping this type feeder-agnostic.
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
            targetSettings = null;
            targetGroup = null;
            feederTargetGroup = null;
            feederTargetAllows = false;
        }
    }
}
