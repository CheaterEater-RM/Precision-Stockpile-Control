using System.Reflection;
using HarmonyLib;
using Verse;

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
            if (PscCap.TryGetRoom(storeCell, map, thing.def, out int room) && room < __result)
                __result = room;
        }
    }
}
