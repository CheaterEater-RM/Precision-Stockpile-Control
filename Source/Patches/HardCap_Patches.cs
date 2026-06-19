using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // M2 hard-cap support. Shared room calculator: how many more items of `def` a PSC-capped unit
    // covering `cell` will accept, computed from the LIVE unit count (the M1 count cache). Returns
    // false when no PSC upper cap applies (caller runs vanilla untouched). All callers early-out on
    // PscStorageDataStore.IsEmpty before this, so vanilla pays ~nothing when no PSC data exists.
    internal static class PscCap
    {
        public static bool TryGetRoom(IntVec3 cell, Map map, ThingDef def, out int room)
        {
            room = int.MaxValue;
            if (def == null || map == null) return false;
            var unit = PscHaulUnit.ResolveCell(cell, map);
            if (!unit.IsValid) return false;
            var data = PscStorageDataStore.TryGet(unit.Settings);
            if (data == null || !data.HasEffectiveLimit(def)) return false;
            var lim = data.GetEffectiveLimit(def);
            if (!lim.Upper.HasValue) return false;
            room = Math.Max(0, lim.Upper.Value - data.GetCount(def, unit));
            return true;
        }
    }

    // Focused hard cap — the carry-drop seam (design §7/§8 M2). When a pawn drops a carried thing
    // into a PSC-capped unit, drop only as many as fit under the unit maximum and leave the rest
    // carried, letting vanilla PlaceHauledThingInCell's existing fallback (find better storage ->
    // haul aside -> last-resort) handle the remainder.
    //
    // This is the *true* hard enforcement: M1 only clamps job.count at plan time, so reservation
    // overshoot / opportunistic duplicates / plan-vs-drop drift could still push a unit over. Because
    // admission (AllowedToAccept) and this drop both read the same live count cache, planning and
    // enforcement stay consistent — once a unit is full, no new haul-to-it jobs are created, so the
    // reduced drop here cannot create a haul loop.
    //
    // Chosen over the design's PlaceHauledThingInCell-lambda transpiler (Stockpile Limit precedent):
    // a tightly-gated prefix on the public no-count overload is more robust (no compiler-lambda
    // reflection) and also catches manual/drafted drops into a capped stockpile. Narrowly gated
    // cancelling prefix (Hard Rule #6) — only acts on drops into a PSC-capped unit.
    [HarmonyPatch]
    public static class Pawn_CarryTracker_TryDropCarriedThing_Patch
    {
        static MethodBase TargetMethod()
        {
            return typeof(Pawn_CarryTracker).GetMethod(nameof(Pawn_CarryTracker.TryDropCarriedThing),
                new[] { typeof(IntVec3), typeof(ThingPlaceMode), typeof(Thing).MakeByRefType(), typeof(Action<Thing, int>) });
        }

        public static bool Prefix(Pawn_CarryTracker __instance, IntVec3 dropLoc, ThingPlaceMode mode,
            ref Thing resultingThing, Action<Thing, int> placedAction, ref bool __result)
        {
            if (PscStorageDataStore.IsEmpty) return true;       // run vanilla
            var carried = __instance.CarriedThing;
            if (carried == null) return true;
            var map = __instance.pawn?.MapHeld;
            if (map == null) return true;

            if (!PscCap.TryGetRoom(dropLoc, map, carried.def, out int room)) return true;
            if (room >= carried.stackCount) return true;        // whole stack fits under the cap

            if (room <= 0)
            {
                // Unit is full — drop nothing, report failure so vanilla's fallback relocates/asides.
                resultingThing = null;
                __result = false;
                return false;
            }

            // Drop only what fits (the count overload routes through innerContainer.TryDrop, NOT this
            // patched no-count overload, so there is no recursion), then report INCOMPLETE so the
            // caller (PlaceHauledThingInCell) relocates the remaining carried items. Mirrors
            // Stockpile Limit's TryDropCarriedThingHooker.
            __instance.TryDropCarriedThing(dropLoc, room, mode, out resultingThing, placedAction);
            __result = false;
            return false;
        }
    }
}
