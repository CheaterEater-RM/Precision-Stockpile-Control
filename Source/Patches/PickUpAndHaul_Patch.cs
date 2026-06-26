using System.Reflection;
using HarmonyLib;
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
