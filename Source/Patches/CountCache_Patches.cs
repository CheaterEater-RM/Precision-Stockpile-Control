using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace PrecisionStockpileControl
{
    // Count-cache drift seams (design §6.1, D18). Each postfix only MARKS dirty — the actual
    // recount from HeldThings happens lazily on the next admission read. Every patch early-outs on
    // PscStorageDataStore.IsEmpty (a single int compare) so vanilla pays ~nothing when no PSC data
    // exists anywhere. The staggered resync in PscMapComponent is the backstop for anything missed.
    internal static class PscCount
    {
        public static void MarkDirty(StorageSettings settings, ThingDef def)
        {
            if (settings == null || def == null) return;
            PscStorageDataStore.TryGet(settings)?.MarkDirty(def);
        }

        public static void MarkAllDirty(StorageSettings settings)
        {
            if (settings == null) return;
            PscStorageDataStore.TryGet(settings)?.MarkAllDirty();
        }

        public static void MarkDirtyForThing(Thing t)
        {
            if (t == null) return;
            var u = PscHaulUnit.ResolveCurrent(t);
            if (u.IsValid) PscStorageDataStore.TryGet(u.Settings)?.MarkDirty(t.def);
        }

        // Drop all reserved-inbound for a unit (regroup / setting-off). Cheap and unconditional —
        // reservations are runtime-only and never valid across a regroup, regardless of the setting.
        public static void ClearReserved(StorageSettings settings)
        {
            if (settings == null) return;
            PscStorageDataStore.TryGet(settings)?.ClearReservedInbound();
        }
    }

    // --- Whole-thing add/remove: buildings ---
    [HarmonyPatch(typeof(Building_Storage), nameof(Building_Storage.Notify_ReceivedThing))]
    public static class Building_Storage_Received_Patch
    {
        public static void Postfix(Building_Storage __instance, Thing newItem)
        {
            if (!PscFeederHaulContext.IsEmpty) PscFeederHaulContext.Clear(newItem);
            if (PscStorageDataStore.IsEmpty) return;
            PscCount.MarkDirty(__instance.GetStoreSettings(), newItem?.def);
        }
    }

    [HarmonyPatch(typeof(Building_Storage), nameof(Building_Storage.Notify_LostThing))]
    public static class Building_Storage_Lost_Patch
    {
        public static void Postfix(Building_Storage __instance, Thing newItem)
        {
            if (PscStorageDataStore.IsEmpty) return;
            PscCount.MarkDirty(__instance.GetStoreSettings(), newItem?.def);
        }
    }

    // --- Whole-thing add/remove: zones ---
    [HarmonyPatch(typeof(Zone_Stockpile), nameof(Zone_Stockpile.Notify_ReceivedThing))]
    public static class Zone_Stockpile_Received_Patch
    {
        public static void Postfix(Zone_Stockpile __instance, Thing newItem)
        {
            if (!PscFeederHaulContext.IsEmpty) PscFeederHaulContext.Clear(newItem);
            if (PscStorageDataStore.IsEmpty) return;
            PscCount.MarkDirty(__instance.GetStoreSettings(), newItem?.def);
        }
    }

    [HarmonyPatch(typeof(Zone_Stockpile), nameof(Zone_Stockpile.Notify_LostThing))]
    public static class Zone_Stockpile_Lost_Patch
    {
        public static void Postfix(Zone_Stockpile __instance, Thing newItem)
        {
            if (PscStorageDataStore.IsEmpty) return;
            PscCount.MarkDirty(__instance.GetStoreSettings(), newItem?.def);
        }
    }

    // --- In-place stackCount changes (no Notify fires) ---
    // Partial split leaves the source spawned with a reduced stackCount.
    [HarmonyPatch(typeof(Thing), nameof(Thing.SplitOff))]
    public static class Thing_SplitOff_Patch
    {
        public static void Postfix(Thing __instance)
        {
            if (PscStorageDataStore.IsEmpty) return;
            if (__instance.Spawned) PscCount.MarkDirtyForThing(__instance);
        }
    }

    // Absorption grows __instance and shrinks/destroys other without a Notify on __instance.
    [HarmonyPatch(typeof(Thing), nameof(Thing.TryAbsorbStack))]
    public static class Thing_TryAbsorbStack_Patch
    {
        public static void Postfix(Thing __instance, Thing other)
        {
            if (PscStorageDataStore.IsEmpty) return;
            PscCount.MarkDirtyForThing(__instance);
            PscCount.MarkDirtyForThing(other); // no-op if other was destroyed (now unspawned)
        }
    }

    // --- Zone cells gain/lose contents wholesale ---
    [HarmonyPatch(typeof(Zone_Stockpile), nameof(Zone_Stockpile.AddCell))]
    public static class Zone_Stockpile_AddCell_Patch
    {
        public static void Postfix(Zone_Stockpile __instance)
        {
            if (PscStorageDataStore.IsEmpty) return;
            PscCount.MarkAllDirty(__instance.GetStoreSettings());
        }
    }

    [HarmonyPatch(typeof(Zone_Stockpile), nameof(Zone_Stockpile.RemoveCell))]
    public static class Zone_Stockpile_RemoveCell_Patch
    {
        public static void Postfix(Zone_Stockpile __instance)
        {
            if (PscStorageDataStore.IsEmpty) return;
            PscCount.MarkAllDirty(__instance.GetStoreSettings());
        }
    }

    // --- Link / unlink: existing contents are not re-"received", so rebuild both units' counts ---
    [HarmonyPatch(typeof(StorageGroupUtility), nameof(StorageGroupUtility.SetStorageGroup))]
    public static class StorageGroupUtility_SetStorageGroup_Patch
    {
        public static void Prefix(IStorageGroupMember member, out StorageGroup __state)
        {
            __state = member?.Group;   // old group (may be null)
        }

        public static void Postfix(IStorageGroupMember member, StorageGroup __state)
        {
            if (PscStorageDataStore.IsEmpty) return;
            if (__state != null) { var s = __state.GetStoreSettings(); PscCount.MarkAllDirty(s); PscCount.ClearReserved(s); }
            if (member is IStoreSettingsParent parent) { var s = parent.GetStoreSettings(); PscCount.MarkAllDirty(s); PscCount.ClearReserved(s); }
            if (member?.Group != null) { var s = member.Group.GetStoreSettings(); PscCount.MarkAllDirty(s); PscCount.ClearReserved(s); }
        }
    }

    // Reserved-inbound DECREMENT at the carry-drop seam (the split-counter's delivery side). When a
    // hauler deposits a carried stack into a tracked capped unit, subtract the ACTUALLY-placed amount
    // (carried stackCount before minus after) from that unit's reserved-inbound. Measuring the delta at
    // the drop catches BOTH a new-stack placement and an absorb-merge into an existing stack (the latter
    // fires no Notify_ReceivedThing), and the narrow gate (Direct mode + a ToCellStorage job dropping at
    // its own target cell) excludes unrelated direct spawns so they can never steal a reservation.
    // The capture prefix runs FIRST so it reads the pre-drop count before HardCap's prefix may do a
    // partial drop; the postfix always runs and reads the post-drop count. State is per-call (Harmony
    // __state), never a static, so it is multiplayer-safe.
    [HarmonyPatch]
    public static class Pawn_CarryTracker_TryDropCarriedThing_ReservePatch
    {
        public struct DropState { public StorageSettings settings; public ThingDef def; public int before; }

        static MethodBase TargetMethod()
            => typeof(Pawn_CarryTracker).GetMethod(nameof(Pawn_CarryTracker.TryDropCarriedThing),
                new[] { typeof(IntVec3), typeof(ThingPlaceMode), typeof(Thing).MakeByRefType(), typeof(Action<Thing, int>) });

        [HarmonyPriority(Priority.First)]
        public static void Prefix(Pawn_CarryTracker __instance, IntVec3 dropLoc, ThingPlaceMode mode, out DropState __state)
        {
            __state = default;
            if (!PscMod.Settings.reservedFillCounting || PscStorageDataStore.IsEmpty) return;
            if (mode != ThingPlaceMode.Direct) return;
            var carried = __instance.CarriedThing;
            if (carried == null) return;
            var pawn = __instance.pawn;
            var job = pawn?.jobs?.curJob;
            if (job == null || job.haulMode != HaulMode.ToCellStorage) return;  // only genuine storage hauls
            if (job.targetB.Cell != dropLoc) return;                            // only the job's own target cell
            var map = pawn.MapHeld;
            if (map == null) return;
            var unit = PscHaulUnit.ResolveCell(dropLoc, map);
            if (!unit.IsValid) return;
            var data = PscStorageDataStore.TryGet(unit.Settings);
            if (data == null || !data.HasLimit(carried.def)) return;
            var lim = data.GetLimit(carried.def);
            if (lim == null || !lim.Upper.HasValue) return;
            __state = new DropState { settings = unit.Settings, def = carried.def, before = carried.stackCount };
        }

        public static void Postfix(Pawn_CarryTracker __instance, DropState __state)
        {
            if (__state.settings == null) return;
            var carried = __instance.CarriedThing;
            int after = (carried != null && carried.def == __state.def) ? carried.stackCount : 0;
            int placed = __state.before - after;
            if (placed <= 0) return;
            var data = PscStorageDataStore.TryGet(__state.settings);
            if (data == null) return;
            data.AddReservedInbound(__state.def, -placed);
            if (PscLog.Enabled) PscLog.Msg(
                $"reserve: -{placed} {__state.def.defName} delivered (now reserved {data.GetReservedInbound(__state.def)})");
        }
    }

    // Reserved-inbound INCREMENT at the commit seam (the split-counter's intake side). A reservation is
    // registered when a haul-to-cell job actually STARTS executing, NOT when its job object is built.
    // The builder HaulAIUtility.HaulToCellStorageJob is also invoked during feasibility probes
    // (WorkGiver_Scanner.HasJobOnThing == JobOnThing(...) != null builds and discards a job), so
    // incrementing there reserved phantom amounts on every scan: it pinned a capped pile's effective
    // count at the cap (all hauling blocked) and desynced HasJobOnThing from JobOnThing (the "provided
    // target ... yielded no actual job" error). JobDriver_HaulToCell.Notify_Starting is reached only on
    // StartJob's real-execution path (after pre-toil reservations succeed and the opportunistic detour
    // resolves), so discarded feasibility jobs never reserve. Resolves the target unit from the same cell
    // the carry-drop decrement uses (job.targetB.Cell), so inc and dec key the same data object. job.count
    // here is already the builder's final clamped count (vanilla space clamp, then PSC cap-room clamp);
    // reserve it verbatim (conservative: errs toward not overshooting). Decremented at the carry-drop seam
    // above; the periodic rebuild in PscMapComponent is the truth backstop, and the decrement's ≤0⇒Remove
    // clamp guards a delivery whose start we never saw (e.g. a haul in flight across a save/load).
    [HarmonyPatch(typeof(JobDriver_HaulToCell), nameof(JobDriver_HaulToCell.Notify_Starting))]
    public static class JobDriver_HaulToCell_NotifyStarting_ReservePatch
    {
        public static void Postfix(JobDriver_HaulToCell __instance)
        {
            if (!PscMod.Settings.reservedFillCounting || PscStorageDataStore.IsEmpty) return;
            var job = __instance.job;
            if (job == null) return;
            var t = job.targetA.Thing;
            if (t == null) return;
            int count = job.count;
            if (count <= 0) return;
            var map = __instance.pawn?.Map;
            if (map == null) return;
            var unit = PscHaulUnit.ResolveCell(job.targetB.Cell, map);
            if (!unit.IsValid) return;
            var data = PscStorageDataStore.TryGet(unit.Settings);
            if (data == null || !data.HasLimit(t.def)) return;
            var lim = data.GetLimit(t.def);
            if (lim == null || !lim.Upper.HasValue) return;
            data.AddReservedInbound(t.def, count);
            if (PscLog.Enabled) PscLog.Msg(
                $"reserve: +{count} {t.def.defName} -> {unit.UniqueLoadID} (now reserved {data.GetReservedInbound(t.def)})");
        }
    }
}
