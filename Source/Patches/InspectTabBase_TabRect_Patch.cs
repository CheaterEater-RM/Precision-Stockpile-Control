using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Keeps the storage tab's window FRAME locked to its CONTENT width so neither is left stranded.
    // The tab has two width sources: the static ITab_Storage.WinSize drives the FillTab content rect
    // AND the filter-tree column (read live each draw), while the per-tab instance field
    // InspectTabBase.size drives the frame via TabRect. PscStartup seeds WinSize to StorageTabWidth,
    // but the ctor copied size=300 during ResolveReferences (before PscStartup ran), so the frame has
    // to be corrected on the live instance.
    //
    // A prefix on the TabRect getter runs *before* the rect is computed, so the very first rendered
    // frame is already correct (no one-frame clip), and it covers the prebuilt shared instance, future
    // instances, and every ITab_Storage subclass uniformly. The `is ITab_Storage` gate also catches
    // vanilla's ITab_Shells / ITab_BiosculpterNutritionStorage — intentional: they share the same
    // filter UI PSC already patches, so they should widen too.
    //
    // Width contract (mod-agnostic): target = max(StorageTabWidth floor, live WinSize.x). Because
    // WinSize.x is where a width mod writes its choice — e.g. Resize Filter Tab overwrites it (and the
    // frame) in an ITab_Storage.OnOpen prefix — reading it live lets PSC DEFER UP to any wider setting
    // while holding its own 360 floor for narrower ones (PSC's button + toggle strip need ~300px). We
    // then write that one target onto BOTH surfaces so the frame and content can never diverge (the
    // previous "pin the frame to 360 while a mod drags content elsewhere" left the dead strip / clip).
    // Gated on StorageTabWidened so we never touch either while the content is still vanilla (fail-safe
    // symmetry with WinSize). The `is` type-check + the `!=` guards in the setters keep this trivial.
    [HarmonyPatch(typeof(InspectTabBase), "TabRect", MethodType.Getter)]
    internal static class InspectTabBase_TabRect_Patch
    {
        public static void Prefix(InspectTabBase __instance)
        {
            if (!PscStartup.StorageTabWidened || !(__instance is ITab_Storage)) return;

            float target = Mathf.Max(PscStartup.StorageTabWidth, PscReflection.GetStorageContentWidth());
            PscReflection.SetStorageContentWidth(target); // static WinSize.x -> content rect + filter tree
            PscReflection.SetTabWidth(__instance, target); // instance size.x -> window frame
        }
    }
}
