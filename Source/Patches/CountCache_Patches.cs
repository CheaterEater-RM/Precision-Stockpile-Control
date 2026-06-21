using HarmonyLib;
using RimWorld;
using Verse;

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
            if (__state != null) PscCount.MarkAllDirty(__state.GetStoreSettings());
            if (member is IStoreSettingsParent parent) PscCount.MarkAllDirty(parent.GetStoreSettings());
            if (member?.Group != null) PscCount.MarkAllDirty(member.Group.GetStoreSettings());
        }
    }
}
