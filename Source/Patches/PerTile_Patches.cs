using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Per-cell ("per-tile") stack limit for FLOOR stockpiles (opt-in: PscSettings.perTileLimits, default
    // off). Caps how many items pile onto one floor cell so haulers spread items thin across the pile
    // (e.g. wood / chemfuel laid out so fire spreads, or just fanning goods out). Floor-only and gated:
    // every patch early-outs via PscPerTile before doing anything, so a colony with the feature off pays
    // only the cheap setting/IsEmpty checks.
    //
    // Mirrors the proven "Stockpile Stack Limit" recipe but in PSC's idiom: tighten-only postfixes
    // wherever possible, with the single narrow prefix used only to make existing over-cap piles
    // relocatable (Scope B). The destination clamp on the haul job, and the exception that lets an
    // over-cap pile spread WITHIN its own stockpile, live in Admission_Patches.cs alongside the existing
    // HaulToCellStorageJob postfix (to compose cleanly with the per-def clamps already there).
    //
    // PlaceSpotQualityAt and TryAbsorbStackNumToTake MUST agree on what "full" means: if the place-spot
    // finder still thinks an at-cap cell is stackable (vanilla counts up to stackLimit) but the merge
    // clamp lets 0 in, placement picks that cell, merges nothing, and fails ("Failed to place ... in
    // mode Near"). So both are patched. PlaceSpotQualityAt returns the PRIVATE nested enum
    // Verse.GenPlace+PlaceSpotQuality which C# can't name; Harmony 2.4 rejects a mismatched __result
    // type (byte), so it binds __result as `object` (Harmony boxes the enum, the documented approach for
    // inaccessible return types) and writes back a reflection-built boxed Unusable.

    // Spreading driver. A floor cell already holding >= cap of this def is "blocked" for more, so the
    // store search skips it and steers the item (a new haul OR a relocated excess) to an emptier cell.
    // Tighten-only: only ever flips a vanilla true to false. PSC does not otherwise patch this method.
    [HarmonyPatch(typeof(StoreUtility), "NoStorageBlockersIn")]
    public static class StoreUtility_NoStorageBlockersIn_PerTile_Patch
    {
        public static void Postfix(ref bool __result, IntVec3 c, Map map, Thing thing)
        {
            if (!__result) return;                       // never override a vanilla block
            if (thing == null) return;
            if (PscPerTile.TryGetCellRoom(c, map, thing.def, out int room) && room <= 0)
                __result = false;
        }
    }

    // Existing-pile relocation (Scope B). An already-stored spawned stack whose own floor cell holds more
    // than the per-tile cap is forced to read as Unstored, so vanilla's IsInValidBestStorage finds the
    // emptier cell NoStorageBlockersIn now steers to and the excess becomes haulable. The actual move
    // (and its excess-only count) is handled by the HaulToCellStorageJob clamps. Separate prefix class
    // from PSC's PscAdmissionScope prefix + fine-order transpiler on the same method (Harmony composes
    // them); it only writes the by-ref currentPriority, so ordering is irrelevant. Untouched for carried /
    // unspawned items (no current cell -> no cap).
    [HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor))]
    public static class StoreUtility_PerTileRelocate_Patch
    {
        public static void Prefix(Thing t, ref StoragePriority currentPriority)
        {
            if (PscStorageDataStore.IsEmpty) return;
            if (PscPerTile.TryGetCellCap(t, out int cap) && t.stackCount > cap)
                currentPriority = StoragePriority.Unstored;
        }
    }

    // Merge cap. Two stacks merging on a floor cell may not push the kept stack over the per-tile cap.
    // Postfix clamps the absorb amount DOWN (tighten-only); covers both Thing.TryAbsorbStack and
    // ThingWithComps.TryAbsorbStack, which both route through TryAbsorbStackNumToTake. Only when
    // respectStackLimit (vanilla's own gate) and the absorber sits in a floor per-tile cell.
    [HarmonyPatch(typeof(ThingUtility), nameof(ThingUtility.TryAbsorbStackNumToTake))]
    public static class ThingUtility_TryAbsorbStackNumToTake_PerTile_Patch
    {
        public static void Postfix(ref int __result, Thing thing, bool respectStackLimit)
        {
            if (!respectStackLimit || __result <= 0) return;
            if (thing == null) return;
            if (PscPerTile.TryGetCellCap(thing, out int cap))
            {
                int room = cap - thing.stackCount;
                if (room < 0) room = 0;
                if (__result > room) __result = room;
            }
        }
    }

    // Drop / produce spread, and the consistency partner to the merge clamp above. A floor cell at or
    // over the per-tile cap is an unusable place spot, so the spot finder skips it (fanning produced /
    // dropped output out, and never picking an at-cap cell only to merge 0 there). __result is bound as
    // `object` because PlaceSpotQuality is a private nested enum (see header); we only ever write back a
    // boxed Unusable (tighten-only). Prepare() fails the patch closed if the enum can't be resolved
    // (future RimWorld), degrading to no drop-spot spreading rather than crashing.
    [HarmonyPatch(typeof(GenPlace), "PlaceSpotQualityAt")]
    public static class GenPlace_PlaceSpotQualityAt_PerTile_Patch
    {
        private static readonly Type QualityType = AccessTools.Inner(typeof(GenPlace), "PlaceSpotQuality");
        // Boxed PlaceSpotQuality.Unusable (value 0). A boxed value of the EXACT enum type is required so
        // Harmony's unbox-back to the return type succeeds.
        private static readonly object UnusableBoxed = QualityType != null ? Enum.ToObject(QualityType, 0) : null;

        public static bool Prepare()
        {
            if (UnusableBoxed != null) return true;
            Log.Warning("[PSC] Could not resolve Verse.GenPlace+PlaceSpotQuality; per-cell drop-spot "
                + "spreading disabled (hauled-item spreading is unaffected). RimWorld version change?");
            return false;
        }

        public static void Postfix(ref object __result, IntVec3 c, Map map, Thing thing)
        {
            if (thing == null) return;
            if (PscPerTile.TryGetCellRoom(c, map, thing.def, out int room) && room <= 0)
                __result = UnusableBoxed;
        }
    }
}
