using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Appends the pure-policy PSC gizmos to a storage BLUEPRINT / construction FRAME so an unbuilt
    // shelf can be configured exactly like the finished one (the policy carries to the building via
    // StorageSettings.CopyFrom). PSC has no runtime effect on the unbuilt shelf; these gizmos only
    // edit data.
    //
    // Included: mode, alarm, subpriority-paint — all pure StorageSettings/PscStorageData policy.
    // Excluded: the FEEDER gizmos, which need a live slot group and a persistent endpoint identity
    // that does not exist until the shelf is built (deferred). The built-storage seams
    // (Feeder_Gizmos_Patch) still own the full set including feeder.
    public static class PscBlueprintGizmos
    {
        public static IEnumerable<Gizmo> For(IStoreSettingsParent parent)
        {
            if (Find.Selector.NumSelected != 1) yield break;
            if (parent is Thing t && !t.Spawned) yield break;
            if (PscStorageButtonFilter.ShouldHide(parent)) yield break;  // opt-out hides gizmos too
            var settings = parent.GetStoreSettings();
            if (settings == null) yield break;

            // Unbuilt storage resolves to an invalid unit (no slot group yet); the factories below are
            // pure-policy and tolerate it. Passed through so the gizmos share the built-path signature.
            var unit = PscHaulUnit.ResolveSettings(settings);
            foreach (var g in PscModeGizmo.GizmosFor(settings, unit)) yield return g;
            foreach (var g in PscAlarmGizmo.GizmosFor(settings, unit)) yield return g;
            foreach (var g in PscSubpriorityPaintGizmo.GizmosFor(parent, settings, unit)) yield return g;
        }
    }

    [HarmonyPatch(typeof(Blueprint_Storage), nameof(Blueprint_Storage.GetGizmos))]
    public static class Blueprint_Storage_GetGizmos_Patch
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Blueprint_Storage __instance)
        {
            foreach (var g in __result) yield return g;
            foreach (var g in PscBlueprintGizmos.For(__instance)) yield return g;
        }
    }

    // A storage Frame (under construction) exposes the storage tab too; give it the same gizmo set so
    // config survives blueprint -> frame -> building. A non-storage frame's GetStoreSettings() is null,
    // so PscBlueprintGizmos.For yields nothing there.
    [HarmonyPatch(typeof(Frame), nameof(Frame.GetGizmos))]
    public static class Frame_GetGizmos_Patch
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Frame __instance)
        {
            foreach (var g in __result) yield return g;
            if (__instance is IStoreSettingsParent parent)
                foreach (var g in PscBlueprintGizmos.For(parent)) yield return g;
        }
    }
}
