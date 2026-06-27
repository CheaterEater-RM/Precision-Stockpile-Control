using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace PrecisionStockpileControl
{
    // Pick Up And Haul integration (design §12, soft-dependency). PUAH plans multi-item inventory
    // hauls using WorkGiver_HaulToInventory.CapacityAt(thing, cell, map). Reduce the reported
    // capacity to the PSC unit's remaining room so PUAH does not over-allocate into a capped unit.
    //
    // Best-effort: PUAH probes several cells of a unit before executing, so transient multi-cell
    // overshoot within one unit is possible; it self-corrects once the unit hits the cap and the
    // carry-drop prefix (HardCap_Patches) blocks further drops. Guarded by Prepare()/TargetMethod via
    // reflection — there is no compile- or load-time reference to PUAH, so the mod loads fine without it.
    [HarmonyPatch]
    public static class PickUpAndHaul_CapacityAt_Patch
    {
        public static bool Prepare() => PscReflection.PuahCapacityAt() != null;

        public static MethodBase TargetMethod() => PscReflection.PuahCapacityAt();

        public static void Postfix(Thing thing, IntVec3 storeCell, Map map, ref int __result)
        {
            if (PscStorageDataStore.IsEmpty || __result <= 0 || thing == null) return;
            // includeReserved: PUAH's capacity probe is a soft-planning read, so subtract in-flight hauls
            // too (no-op when the feature is off). PUAH bulk jobs don't register reserved themselves, so
            // this is best-effort, backstopped by the carry-drop hard cap.
            if (PscCap.TryGetRoom(storeCell, map, thing.def, out int room, includeReserved: true) && room < __result)
                __result = room;
        }
    }

    // PUAH bulk-gather destination adapter (design §12 Phase 4, soft-dependency). PUAH's bulk planner picks
    // destinations for the EXTRA items it gathers via its OWN private WorkGiver_HaulToInventory.TryFindBestBetterStoreCellFor,
    // which gates on `slotGroup.Settings.Priority <= currentPriority` -- a STRICTLY-higher vanilla-band test. PSC
    // feeder routing is same-vanilla-band fine-order (e.g. source rank 126 -> chain node rank 104/105), so PUAH's
    // planner is blind to it: it never plans the same-band chain hop, returns no extra-item target, and the bulk
    // gather collapses to the primary item's single vanilla haul (the "won't grab a bunch from 1 to 2" symptom).
    //
    // Delegate that choice to PSC's store-search engine, which the primary item already flows through (vanilla
    // TryFindBestBetterStorageFor at JobOnThing). The engine resolves the item's spawned source and applies PSC's
    // full-key (band, then fine-order) selection, so PUAH can finally plan bulk hauls INTO the chain; and because
    // the engine refuses a feeder source's non-destination targets, it also bounds the gather to what the chain can
    // accept (NoLegalTarget instead of the overflow), which is half of "don't over-pick-up". PUAH's skipCells (the
    // cells already allocated this gather) ride PscSearchOptions.ExcludedCells so successive calls advance.
    //
    // Guarded by Prepare()/TargetMethod via reflection — no compile- or load-time reference to PUAH.
    [HarmonyPatch]
    public static class PickUpAndHaul_ExtraItemStoreCell_Patch
    {
        public static bool Prepare() => PscReflection.PuahExtraItemStoreCell() != null;

        public static MethodBase TargetMethod() => PscReflection.PuahExtraItemStoreCell();

        // Mirrors PUAH's signature; returns false to suppress PUAH's band-blind body when PSC owns the decision.
        public static bool Prefix(Thing thing, Pawn carrier, Map map, StoragePriority currentPriority,
            Faction faction, ref IntVec3 foundCell, ref bool __result)
        {
            // Cheapest gate first: no PSC policy anywhere -> let PUAH's own band-search run unchanged (and skip
            // the engine teardown, since nothing was set).
            if (PscStorageDataStore.IsEmpty || thing == null || map == null) return true;

            var skip = PscReflection.PuahSkipCells();
            var options = new PscSearchOptions(skip, needAccurateResult: true, PscSearchCaller.PuahExtraItem);
            try
            {
                var res = PscStoreSearchEngine.TryFindBestStoreCell(thing, carrier, map, currentPriority, faction,
                    options, out var cell);
                if (res == PscStoreSearchEngine.PscSearchResult.Unaffected)
                    return true;   // PSC not active for this item (non-PSC map / DSU-resident) -> run PUAH's own body

                if (res == PscStoreSearchEngine.PscSearchResult.Found)
                {
                    foundCell = cell;
                    __result = true;
                    skip?.Add(cell);   // replicate PUAH's own skipCells.Add so its allocation loop advances
                }
                else   // NoLegalTarget: PSC owns this item and found nowhere legal. Do NOT let PUAH's band-search
                {      // pick a worse destination (e.g. the overflow) -- report no cell so the gather stops here.
                    foundCell = IntVec3.Invalid;
                    __result = false;
                }
                return false;      // suppress PUAH's body
            }
            finally
            {
                // A direct engine call never reaches StoreUtility_Engine_Patch.Finalizer, so tear the per-search
                // state down here, or VanillaFallbackPlanning / the carrier memo / candidate buffers leak forward.
                PscStoreSearchEngine.ResetSearchState();
            }
        }
    }

    // Feeder-source provenance capture for PUAH bulk gathers (soft-dependency). PUAH carries items through
    // a pawn's INVENTORY via SplitOff + merge, which severs the source link PSC's feeder admission needs:
    // by unload the item is unspawned with no source, so the gate can neither admit it into an
    // onlyFromSource chain node nor hold it out of the overflow (the items "forget where they came from").
    //
    // TryMakePreToilReservations is the COMMITTED seam (reservations succeeded, job about to run) where the
    // queued items are still spawned in their source. Snapshot each feeder-sourced item's origin into
    // PscPuahSourceTracker, keyed by (pawn, def) count segments so the later merge can't defeat it; the
    // restore reads it back during the unload store-search (PscAdmissionIndex.TryFeederReject). Guarded by
    // Prepare()/TargetMethod via reflection — no compile- or load-time reference to PUAH.
    [HarmonyPatch]
    public static class PickUpAndHaul_HaulToInventory_Capture_Patch
    {
        public static bool Prepare() => PscReflection.PuahHaulToInventoryReserve() != null;

        public static MethodBase TargetMethod() => PscReflection.PuahHaulToInventoryReserve();

        public static void Postfix(JobDriver __instance, bool __result)
        {
            if (!__result || PscStorageDataStore.IsEmpty) return;   // reservations failed -> job won't run
            var job = __instance?.job;
            var pawn = __instance?.pawn;
            if (job == null || pawn == null) return;
            var map = pawn.Map;
            if (map == null) return;
            var psc = PscMapComponent.For(map);
            if (psc == null || !psc.anyFeederActive) return;

            // TryMakePreToilReservations is not one-shot: skip a re-fire of a job already captured for this pawn,
            // or its queue is recorded again as duplicate segments.
            if (PscPuahSourceTracker.AlreadyCapturedJob(pawn, job.loadID)) return;

            var queue = job.targetQueueA;
            if (queue == null) return;
            var counts = job.countQueue;
            int tick = Find.TickManager?.TicksGame ?? 0;

            for (int i = 0; i < queue.Count; i++)
            {
                var thing = queue[i].Thing;
                if (thing == null || !thing.Spawned) continue;
                var src = PscHaulUnit.ResolveCurrent(thing);
                if (!src.IsValid) continue;
                string srcId = src.UniqueLoadID;
                if (srcId == null) continue;

                // Only feeder-relevant sources are worth remembering: a source that restricts its outflow
                // (onlyToDestinations) or that feeds a chain (has an outgoing route). Everything else is an
                // ordinary haul PSC does not steer, so we leave the tracker untouched and pay nothing. The
                // strictness is re-read LIVE at restore, so this is only a "worth recording at all" gate.
                var sdata = PscStorageDataStore.TryGet(src.Settings);
                bool feederSource = (sdata != null && sdata.onlyToDestinations) || psc.Links.HasAnyDestination(srcId);
                if (!feederSource) continue;

                int count = counts != null && i < counts.Count && counts[i] > 0 ? counts[i] : thing.stackCount;
                PscPuahSourceTracker.Capture(pawn, thing.def, map, srcId, count, tick);
            }
        }
    }
}
