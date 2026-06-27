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
        // The shared cell-keyed resolve+gate: cell -> covering PSC unit + its data + an UPPER-capped limit
        // for `def`. Returns false (all outs null/default) unless the unit has a per-def maximum for def.
        // The three cell-keyed cap sites — drop-room (TryGetRoom), reserve increment, reserve decrement —
        // all funnel through here so the "resolve the unit, gate on an upper cap" contract lives in one
        // place and can't drift between them.
        public static bool TryGetUpperLimit(IntVec3 cell, Map map, ThingDef def,
            out PscHaulUnit unit, out PscStorageData data, out PscDefLimit lim)
        {
            unit = default; data = null; lim = null;
            if (def == null || map == null) return false;
            unit = PscHaulUnit.ResolveCell(cell, map);
            if (!unit.IsValid) return false;
            data = PscStorageDataStore.TryGet(unit.Settings);
            if (data == null || !data.HasEffectiveLimit(def)) return false;   // per-def OR a limit group
            lim = data.GetEffectiveLimit(def);                                // group's shared limit if grouped
            if (lim == null || !lim.Upper.HasValue) return false;
            return true;
        }

        // includeReserved: SOFT-planning callers (PUAH / Hauler's Dream capacity probes) pass true so the
        // returned room reflects in-flight hauls (effective = physical + reserved-inbound) and those mods
        // don't over-allocate into a unit other haulers are already filling. The HARD carry-drop caller
        // leaves it false (physical only) — a stale reservation must never make the drop seam strand a
        // carried item.
        public static bool TryGetRoom(IntVec3 cell, Map map, ThingDef def, out int room, bool includeReserved = false)
        {
            room = int.MaxValue;
            if (!TryGetUpperLimit(cell, map, def, out var unit, out var data, out var lim)) return false;
            // Always returns room in ITEMS, even for a stacks-mode group (where upper is in stacks): the
            // helper does the member-specific stacks->items conversion. For a grouped def it enforces the
            // pooled total, not one member; for an ungrouped def it is the plain per-def room.
            bool eff = includeReserved && PscMod.Settings.reservedFillCounting;
            room = data.GroupAwareItemRoom(def, unit, lim.Upper.Value, eff);
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
    // reflection). It enforces only the storage-mode `Direct` drop seam (the normal haul path); it does
    // NOT cap Near/Radius forced/cleanup/manual/drafted drops — those ignore the return value and have no
    // relocate fallback, so capping them would strand or destroy the carried item (see the mode gate
    // below). No manual or drafted player drop reaches the Direct seam anyway (drafted drop uses Near).
    // Narrowly gated cancelling prefix (Hard Rule #6) — only acts on Direct drops into a PSC-capped unit.
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
            // Only enforce at the Direct drop seam. The normal haul path (Toils_Haul.
            // PlaceHauledThingInCell) drops with ThingPlaceMode.Direct and checks the returned bool,
            // running a find-better -> haul-aside fallback on our false/incomplete return — so the
            // cap holds AND the remainder is relocated. The Near/Radius callers (Pawn_JobTracker.
            // CleanupCurrentJob, Pawn.DropAndForbidEverything on death/downing/capture, mental-break
            // start, force-eject, drafted drop) IGNORE the bool and have no fallback; vanilla Near
            // would spill the stack to an adjacent free cell. Capping them here would instead strand
            // the item in the carry tracker (and on death destroy it with cleanup — silent loss).
            // Let vanilla place those normally; the cap is already enforced at haul-plan time and the
            // Direct seam, and a forced/cleanup drop is an evacuation path that must never strand.
            if (mode != ThingPlaceMode.Direct) return true;
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
