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

        // Phase 1 (1A): per-search memo of the invariant "does the item's SOURCE unit still accept
        // it?" check (source.Settings.AllowedToAccept(t)). Source + item are invariant within one
        // search, so this boolean is too, yet it is otherwise evaluated per candidate in the feeder
        // onlyToDestinations and batchEmpty evacuable exemptions (PscAdmissionIndex). 0 = unprobed,
        // 1 = accepts, 2 = rejects. Reset on item change in EnsureFor and per search in Clear.
        [System.ThreadStatic] private static byte sourceAcceptsState;

        // Opt B3 feeder-decision MRU (single entry, keyed by the target unit's group). Within one search the
        // source is invariant, so FeederAllows(source, target) varies only by target; cache the last
        // (target.group -> allows) result. Reset on item change in EnsureFor (a new item = a new source) and
        // per search in Clear.
        [System.ThreadStatic] private static ISlotGroup feederTargetGroup;
        [System.ThreadStatic] private static bool feederTargetAllows;

        // The hauling pawn for this search (carrier), set by the engine entry. Per SEARCH, not per item, so it
        // is reset only in Clear (not EnsureFor). The carried-item feeder restore (PscPuahSourceTracker) needs
        // it to look up (carrier, def) provenance; non-engine callers (the AllowedToAccept backstop) leave it
        // null and so never restore.
        [System.ThreadStatic] private static Pawn carrier;

        // Restored carried-source memo (PUAH bulk haul): for an item with no live source, the captured feeder
        // source for (carrier, def), resolved once per item. 0 = unprobed, 1 = found, 2 = none. `restoredHoldBack`
        // is precomputed at probe time (source onlyToDestinations AND still allows the def) so the hold-back
        // decision never re-enters AllowedToAccept per candidate. Per ITEM, so reset in EnsureFor and Clear.
        [System.ThreadStatic] private static byte restoredState;
        [System.ThreadStatic] private static string restoredSourceId;
        [System.ThreadStatic] private static bool restoredHoldBack;

        private static void EnsureFor(Thing t)
        {
            if (ReferenceEquals(t, item)) return;
            item = t;
            feederDestState = 0;
            sourceAcceptsState = 0;
            feederTargetGroup = null;
            restoredState = 0;
            restoredSourceId = null;
            restoredHoldBack = false;
            var cur = PscHaulUnit.ResolveCurrent(t);
            sourceValid = cur.IsValid;
            sourceGroup = cur.group;
            sourceData = sourceValid ? PscStorageDataStore.TryGet(cur.Settings) : null;
        }

        // The hauling pawn for this search (or null). Set once by the engine entry; read by the carried-item
        // feeder restore.
        public static Pawn Carrier => carrier;
        public static void SetCarrier(Pawn p) => carrier = p;

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

        // Phase 1 (1A): memoised source.Settings.AllowedToAccept(t) for the search's invariant
        // source, computed once and reused by the per-candidate evacuable exemptions on the planning
        // (engine) path. Returns false for a loose/unspawned/carried item (no source) — the callers
        // only consult it when source.IsValid, so a false here for a no-source item is never read.
        public static bool SourceAcceptsItem(Thing t)
        {
            EnsureFor(t);
            if (sourceAcceptsState == 0)
            {
                var s = sourceValid ? sourceGroup?.Settings : null;
                sourceAcceptsState = (s != null && s.AllowedToAccept(t)) ? (byte)1 : (byte)2;
            }
            return sourceAcceptsState == 1;
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

        // Carried-source restore memo: the captured feeder source for the current (carrier, item-def), or
        // null when none, plus the precomputed hold-back decision. Resolved once per item by TryFeederReject
        // (which holds the map component + tracker) and reused across every candidate. Returns true when a
        // source is already known this item; `probed` distinguishes "known to be none" (2) from "unprobed" (0).
        public static bool TryGetRestoredSource(Thing t, out string sourceId, out bool holdBack, out bool probed)
        {
            EnsureFor(t);
            sourceId = restoredSourceId;
            holdBack = restoredHoldBack;
            probed = restoredState != 0;
            return restoredState == 1;
        }

        public static void CacheRestoredSource(Thing t, string sourceId, bool holdBack)
        {
            EnsureFor(t);
            restoredSourceId = sourceId;
            restoredHoldBack = holdBack;
            restoredState = sourceId != null ? (byte)1 : (byte)2;
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
            sourceAcceptsState = 0;
            feederTargetGroup = null;
            feederTargetAllows = false;
            carrier = null;
            restoredState = 0;
            restoredSourceId = null;
            restoredHoldBack = false;
        }
    }
}
