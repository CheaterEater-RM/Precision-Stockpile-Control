using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Appends PSC's storage-mode gizmo (M5.2) and feeder gizmos (M3) to stockpile zones and storage
    // buildings (shelves + mod storage that extends Building_Storage). Both seams are the vanilla
    // GetGizmos overrides confirmed in source. Gated to a single selection so click-to-target is
    // unambiguous.

    [HarmonyPatch(typeof(Zone_Stockpile), nameof(Zone_Stockpile.GetGizmos))]
    public static class Zone_Stockpile_GetGizmos_FeederPatch
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Zone_Stockpile __instance)
        {
            foreach (var g in __result) yield return g;
            if (Find.Selector.NumSelected != 1) yield break;
            var settings = __instance.GetStoreSettings();
            var unit = PscHaulUnit.ResolveSettings(settings);
            if (!unit.IsValid) yield break;
            foreach (var g in PscModeGizmo.GizmosFor(settings, unit)) yield return g;
            foreach (var g in PscAlarmGizmo.GizmosFor(settings, unit)) yield return g;
            foreach (var g in PscFeederGizmos.GizmosFor(settings, unit)) yield return g;
        }
    }

    [HarmonyPatch(typeof(Building_Storage), nameof(Building_Storage.GetGizmos))]
    public static class Building_Storage_GetGizmos_FeederPatch
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Building_Storage __instance)
        {
            foreach (var g in __result) yield return g;
            if (Find.Selector.NumSelected != 1) yield break;
            if (!__instance.Spawned) yield break;
            var settings = __instance.GetStoreSettings();
            var unit = PscHaulUnit.ResolveSettings(settings);
            if (!unit.IsValid) yield break;
            foreach (var g in PscModeGizmo.GizmosFor(settings, unit)) yield return g;
            foreach (var g in PscAlarmGizmo.GizmosFor(settings, unit)) yield return g;
            foreach (var g in PscFeederGizmos.GizmosFor(settings, unit)) yield return g;
        }
    }
}
