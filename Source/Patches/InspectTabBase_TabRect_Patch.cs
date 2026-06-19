using HarmonyLib;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Widens the storage tab's window FRAME to match the content (which PscStartup widened via the
    // static ITab_Storage.WinSize). The frame is built from InspectTabBase.TabRect off the per-tab
    // instance field `size`, captured at 300 in the ctor when the shared instance was created during
    // ResolveReferences — before PscStartup ran — so it has to be corrected on the live instance.
    //
    // A prefix on the TabRect getter runs *before* the rect is computed, so the very first rendered
    // frame is already wide (no one-frame clip), and it covers the prebuilt shared instance, future
    // instances, and every ITab_Storage subclass uniformly. The `is ITab_Storage` gate also catches
    // vanilla's ITab_Shells / ITab_BiosculpterNutritionStorage — intentional: they share the same
    // filter UI PSC already patches, so they should widen too. Gated on StorageTabWidened so we
    // never widen the frame while the content is still 300 (fail-safe symmetry with WinSize). The
    // `is` type-check + the `s.x != width` guard in SetTabWidth keep this per-frame call trivial.
    [HarmonyPatch(typeof(InspectTabBase), "TabRect", MethodType.Getter)]
    internal static class InspectTabBase_TabRect_Patch
    {
        public static void Prefix(InspectTabBase __instance)
        {
            if (PscStartup.StorageTabWidened && __instance is ITab_Storage)
                PscReflection.SetTabWidth(__instance, PscStartup.StorageTabWidth);
        }
    }
}
