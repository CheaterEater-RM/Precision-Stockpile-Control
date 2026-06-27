using Verse;
using Verse.AI;

namespace PrecisionStockpileControl
{
    // Shared feeder-source provenance capture for a bulk inventory-hauler's gather job, used by BOTH the Pick
    // Up And Haul (JobDriver_HaulToInventory, targetQueueA) and Hauler's Dream (JobDriver_BulkHaul, targetQueueB)
    // capture patches. TryMakePreToilReservations is the COMMITTED seam (reservations succeeded, job about to
    // run) where the queued pickup items are still spawned in their source, so PSC snapshots each feeder-sourced
    // item's origin into PscCarriedSourceTracker -- keyed by (pawn, def) count segments so the later SplitOff +
    // inventory merge can't defeat it. The restore reads it back during the unload store-search
    // (PscAdmissionIndex.TryFeederReject). The two haulers differ ONLY in which target queue holds the pickup
    // targets, so they share this one body and pass their queue index.
    internal static class PscCarriedSourceCapture
    {
        // queueIndex: the target queue holding the pickup targets (read via vanilla Job.GetTargetQueue) --
        // TargetIndex.A for PUAH, TargetIndex.B for HD. Both pair it with job.countQueue for the planned counts.
        public static void CaptureQueue(JobDriver __instance, bool __result, TargetIndex queueIndex)
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
            if (PscCarriedSourceTracker.AlreadyCapturedJob(pawn, job.loadID)) return;

            var queue = job.GetTargetQueue(queueIndex);
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
                PscCarriedSourceTracker.Capture(pawn, thing.def, map, srcId, count, tick);
            }
        }
    }
}
