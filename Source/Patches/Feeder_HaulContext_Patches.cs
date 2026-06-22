using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace PrecisionStockpileControl
{
    // Register the planned source -> destination route at the COMMITTED job-start seam (F3), not in the
    // HaulToCellStorageJob builder. The builder also runs during discarded WorkGiver.HasJobOnThing
    // feasibility probes, which would leak a route keyed on a Thing whose job never starts; Notify_Starting
    // is reached only on StartJob's real-execution path (before the pickup toil), so the item is still
    // spawned in its source and a probe never registers. Mirrors the reserved-inbound increment seam.
    [HarmonyPatch(typeof(JobDriver_HaulToCell), nameof(JobDriver_HaulToCell.Notify_Starting))]
    public static class JobDriver_HaulToCell_NotifyStarting_FeederPatch
    {
        public static void Postfix(JobDriver_HaulToCell __instance)
        {
            if (PscStorageDataStore.IsEmpty) return;
            var job = __instance.job;
            if (job == null || job.haulMode != HaulMode.ToCellStorage) return;
            var t = job.targetA.Thing;
            if (t == null) return;
            var map = __instance.pawn?.Map;
            if (map == null) return;
            var psc = PscMapComponent.For(map);
            if (psc == null || !psc.anyFeederActive) return;
            var source = PscHaulUnit.ResolveCurrent(t);            // still spawned in source at job start
            var target = PscHaulUnit.ResolveCell(job.targetB.Cell, map);
            if (source.IsValid && target.IsValid && psc.FeederAllows(source, target))
                PscFeederHaulContext.Register(t, map, source.UniqueLoadID, target.UniqueLoadID);
        }
    }

    [HarmonyPatch(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.TryStartCarry),
        new[] { typeof(Thing), typeof(int), typeof(bool) })]
    public static class Pawn_CarryTracker_TryStartCarry_FeederContextPatch
    {
        public static void Postfix(Pawn_CarryTracker __instance, Thing item, int __result)
        {
            if (__result <= 0) return;
            if (PscFeederHaulContext.IsEmpty) return;   // nothing in flight -> nothing to transfer
            PscFeederHaulContext.Transfer(item, __instance.CarriedThing);
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.SpawnSetup))]
    public static class Thing_SpawnSetup_FeederContextPatch
    {
        public static void Postfix(Thing __instance)
        {
            // Cheapest gate first (AGENTS.md §6.2): with no feeder haul in flight there is no route to
            // clear, so skip the GetSlotGroup resolution that would otherwise run on EVERY item spawn.
            if (PscFeederHaulContext.IsEmpty) return;
            if (__instance?.def?.category != ThingCategory.Item) return;
            if (PscHaulUnit.ResolveCurrent(__instance).IsValid) return;
            PscFeederHaulContext.Clear(__instance);
        }
    }
}
