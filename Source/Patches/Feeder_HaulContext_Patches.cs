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
            PscFeederHaulContext.Transfer(item, __instance.CarriedThing);
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.SpawnSetup))]
    public static class Thing_SpawnSetup_FeederContextPatch
    {
        public static void Postfix(Thing __instance)
        {
            if (__instance?.def?.category != ThingCategory.Item) return;
            if (PscHaulUnit.ResolveCurrent(__instance).IsValid) return;
            PscFeederHaulContext.Clear(__instance);
        }
    }
}
