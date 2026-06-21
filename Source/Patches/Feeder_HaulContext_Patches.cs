using HarmonyLib;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
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
