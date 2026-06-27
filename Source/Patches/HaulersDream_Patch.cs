using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace PrecisionStockpileControl
{
    // Hauler's Dream integration (soft dependency, mirrors the PUAH CapacityAt patch). HD plans bulk
    // inventory hauls and sizes each per destination via BulkHaul.StorageSpaceForDef(pawn, thing, cell,
    // map). Clamp that to the PSC unit's remaining room — effective (physical + reserved-inbound) when
    // reservation-aware fill counting is on, physical otherwise — so HD does not over-allocate into a
    // capped unit.
    //
    // Best-effort, exactly like PUAH: HD's bulk jobs do NOT route through HaulToCellStorageJob, so they
    // never register reserved themselves, and HD probes several cells before executing — transient
    // overshoot within one unit self-corrects once the unit hits the cap and the carry-drop hard cap
    // (HardCap_Patches) blocks further drops. HD's destination *selection* already routes through
    // StorageSettings.AllowedToAccept (covered by the admission postfix), so even without this quantity
    // clamp HD won't target an effectively-full unit. Guarded by Prepare()/TargetMethod via reflection;
    // no compile- or load-time reference to HD, so PSC loads fine without it (and degrades safely if a
    // future HD version renames the private method).
    [HarmonyPatch]
    public static class HaulersDream_StorageSpaceForDef_Patch
    {
        public static bool Prepare() => PscReflection.HaulersDreamStorageSpaceForDef() != null;

        public static MethodBase TargetMethod() => PscReflection.HaulersDreamStorageSpaceForDef();

        public static void Postfix(Thing thing, IntVec3 cell, Map map, ref int __result)
        {
            if (PscStorageDataStore.IsEmpty || __result <= 0 || thing == null) return;
            if (PscCap.TryGetRoom(cell, map, thing.def, out int room, includeReserved: true) && room < __result)
                __result = room;
        }
    }

    // Feeder-source provenance capture for HD bulk gathers (soft-dependency; the HD analogue of PUAH's
    // HaulToInventory capture patch). HD's JobDriver_BulkHaul carries items through a pawn's INVENTORY via
    // SplitOff + merge (DepositSwept), which severs the source link PSC's feeder admission needs: by unload the
    // item is unspawned with no source, so the gate can neither admit it into an onlyFromSource chain node nor
    // hold it out of the overflow. TryMakePreToilReservations is the committed seam where the queued pickup
    // items are still spawned in their source. HD keeps those targets in targetQueueB (its StackInd), so the
    // shared PscCarriedSourceCapture helper reads TargetIndex.B; the restore reads it back during the unload
    // store-search (PscAdmissionIndex.TryFeederReject). The capacity clamp above plus PSC's admission and
    // same-band store-search already cover HD; this closes the one remaining gap (feeder routing across the
    // inventory merge). Guarded by Prepare()/TargetMethod via reflection — no compile- or load-time reference
    // to HD, and a future HD rename of the private method makes Prepare() return null so the capture silently
    // degrades (carried items shake out via normal hauling) without crashing.
    [HarmonyPatch]
    public static class HaulersDream_BulkHaul_Capture_Patch
    {
        public static bool Prepare() => PscReflection.HaulersDreamBulkHaulReserve() != null;

        public static MethodBase TargetMethod() => PscReflection.HaulersDreamBulkHaulReserve();

        public static void Postfix(JobDriver __instance, bool __result)
            => PscCarriedSourceCapture.CaptureQueue(__instance, __result, TargetIndex.B);
    }
}
