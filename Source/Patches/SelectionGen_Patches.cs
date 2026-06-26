using HarmonyLib;
using RimWorld;

namespace PrecisionStockpileControl
{
    // Selection-cache invalidation chokepoint (store-search rewrite, Phase 3a). The single vanilla method every
    // priority / order change funnels through: the StorageSettings.Priority setter calls it for every owner type
    // (StorageGroup / HaulDestinationOwner / HaulSourceOwner, StorageSettings.cs:38/45/49), and PSC calls it for
    // fine-order edits (NotifyOrderChanged, ResortAllMaps, FinalizeInit). A band or fine-order change flips
    // PscOrder.Outranks, which the feeder-decision cache depends on, but does NOT bump the feeder graph
    // generation. So this is where the per-map selectionGen is bumped (the D3 trap). Cheap: a rare UI / lifecycle
    // event (priority drag, fine-order edit, load re-sort), never per-tick.
    [HarmonyPatch(typeof(HaulDestinationManager),
        nameof(HaulDestinationManager.Notify_HaulDestinationChangedPriority))]
    public static class HaulDestinationManager_Notify_Priority_Patch
    {
        public static void Postfix(HaulDestinationManager __instance)
        {
            if (PscStorageDataStore.IsEmpty) return;        // no PSC anywhere: nothing caches, skip the resolve
            // The owning-map seam is centralized in PscReflection (resolve-once, log-once, degrade). If the
            // private field is ever renamed it returns null; over-invalidate all maps then, so a vanished seam
            // never leaves a stale "functional edge" verdict cached (over-invalidation is always safe).
            var map = PscReflection.GetHaulDestinationMap(__instance);
            if (map != null)
                PscMapComponent.For(map)?.BumpSelectionGen();
            else
                PscMapComponent.BumpSelectionGenAllMaps();
        }
    }
}
