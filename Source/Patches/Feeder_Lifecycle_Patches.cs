using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Session-only payload that carries feeder link endpoints across a copy/paste. Mirrors vanilla's
    // own static StorageSettingsClipboard (transient, not scribed): the clipboard StorageSettings is
    // ownerless and has no UniqueLoadID, so MapComponent-keyed links cannot ride StorageSettings.CopyFrom
    // (which carries the policy flags). They ride this instead.
    internal static class PscLinkClipboard
    {
        public static readonly List<string> Sources = new List<string>();
        public static readonly List<string> Dests = new List<string>();
        public static bool HasData;

        public static void Capture(PscHaulUnit unit)
        {
            Sources.Clear();
            Dests.Clear();
            HasData = false;
            if (!unit.IsValid) return;
            var psc = PscMapComponent.For(unit.Map);
            if (psc == null) return;
            string id = unit.UniqueLoadID;
            if (id == null) return;
            psc.Links.CollectEndpoints(id, Sources, Dests);
            HasData = true;   // even when empty: paste should clear the target's links to match the source
        }
    }

    [HarmonyPatch(typeof(StorageSettingsClipboard), nameof(StorageSettingsClipboard.Copy))]
    public static class StorageSettingsClipboard_Copy_FeederPatch
    {
        public static void Postfix(StorageSettings s)
        {
            PscLinkClipboard.Capture(PscHaulUnit.ResolveSettings(s));
        }
    }

    [HarmonyPatch(typeof(StorageSettingsClipboard), nameof(StorageSettingsClipboard.PasteInto))]
    public static class StorageSettingsClipboard_PasteInto_FeederPatch
    {
        public static void Postfix(StorageSettings s)
        {
            if (!PscLinkClipboard.HasData) return;
            var unit = PscHaulUnit.ResolveSettings(s);
            if (!unit.IsValid) return;
            var psc = PscMapComponent.For(unit.Map);
            psc?.ApplyClipboardLinks(unit, PscLinkClipboard.Sources, PscLinkClipboard.Dests);
        }
    }

    // Vanilla link/unlink of storage groups (buildings only — zones never join groups). Vanilla
    // transfers the StorageSettings (and our policy flags via the CopyFrom postfix); this transfers
    // the feeder links, which are MapComponent-side. On join, the group adopts the standalone
    // member's links; on unlink, the departing member keeps a copy of the group's links.
    public sealed class PscStorageGroupChange
    {
        public StorageGroup oldGroup;
        public StorageSettings oldSettings;
    }

    [HarmonyPatch(typeof(StorageGroupUtility), nameof(StorageGroupUtility.SetStorageGroup))]
    public static class StorageGroupUtility_SetStorageGroup_FeederPatch
    {
        public static void Prefix(IStorageGroupMember member, out PscStorageGroupChange __state)
        {
            __state = new PscStorageGroupChange
            {
                oldGroup = member?.Group,
                oldSettings = member?.StoreSettings
            };
        }

        public static void Postfix(IStorageGroupMember member, StorageGroup newGroup, PscStorageGroupChange __state)
        {
            if (member == null) return;
            var map = member.Map;
            if (map == null) return;
            var psc = PscMapComponent.For(map);
            if (psc == null) return;

            // Standalone id == the building's persistent thing id (matches PscHaulUnit.UniqueLoadID
            // for a standalone slot group: slot.parent.GetUniqueLoadID()).
            string memberId = (member as ILoadReferenceable)?.GetUniqueLoadID();
            if (memberId == null) return;

            if (newGroup != null)                           // joined
            {
                psc.Links.AdoptLinks(memberId, newGroup.GetUniqueLoadID());
                MergeFeederFlags(__state?.oldSettings, newGroup.GetStoreSettings());
                // Member now resolves to the group; drop its standalone edges (group holds them).
                psc.Links.RemoveAllFor(memberId);
            }
            else if (newGroup == null && __state?.oldGroup != null)   // left: keep a copy of the group's links
            {
                psc.Links.AdoptLinks(__state.oldGroup.GetUniqueLoadID(), memberId);
            }
            psc.PruneFeederLinksAndFlags();
        }

        private static void MergeFeederFlags(StorageSettings from, StorageSettings to)
        {
            var src = PscStorageDataStore.TryGet(from);
            if (src == null || (!src.onlyFromSource && !src.onlyToDestinations)) return;
            var dst = PscStorageDataStore.GetOrCreate(to);
            if (src.onlyFromSource) dst.onlyFromSource = true;
            if (src.onlyToDestinations) dst.onlyToDestinations = true;
            PscMapComponent.NotifyPolicyChanged(to);
        }
    }

    [HarmonyPatch(typeof(Zone), nameof(Zone.PostDeregister))]
    public static class Zone_PostDeregister_FeederPatch
    {
        public static void Postfix(Zone __instance)
        {
            if (!(__instance is Zone_Stockpile stockpile)) return;
            var psc = PscMapComponent.For(__instance.Map);
            psc?.RemoveFeederEndpoint(__instance.GetUniqueLoadID());
            // A zone is always standalone (zones never join StorageGroups), so its StorageSettings is
            // uniquely its own — drop the store entry so a deleted stockpile's policy doesn't linger
            // (keeping IsEmpty false and pinning the orphaned settings) for the rest of the session.
            PscStorageDataStore.Remove(stockpile.GetStoreSettings());
        }
    }

    // NO DeSpawn patch (deliberately). A Building_Storage despawn is usually TEMPORARY — minify /
    // uninstall to relocate it (MinifyUtility.MakeMinified calls DeSpawnOrDeselect, never Destroy, and
    // keeps the building as MinifiedThing.InnerThing, so its StorageSettings instance AND persistent
    // thing id survive). Pruning on DeSpawn would silently drop the unit's feeder routes (its endpoint
    // id is momentarily not live) even though the very same id returns on reinstall. A route to a
    // temporarily-absent endpoint is harmless — HasFunctionalFeederEdge resolves only live units, so it
    // simply carries nothing until the storage is back. Permanent removal is handled by Destroy below;
    // a grouped member's despawn is still pruned via StorageGroupManager.Notify_MemberRemoved.

    [HarmonyPatch(typeof(Building_Storage), nameof(Building_Storage.Destroy))]
    public static class Building_Storage_Destroy_FeederPatch
    {
        public static void Prefix(Building_Storage __instance, out PscBuildingDestroyState __state)
        {
            __state = new PscBuildingDestroyState
            {
                map = __instance.Spawned ? __instance.Map : null,
                // Only a STANDALONE building owns its StorageSettings; a grouped member's policy lives on
                // the shared StorageGroup settings that the other members still use, so never drop that.
                ownSettings = (__instance as IStorageGroupMember)?.Group == null
                    ? __instance.GetStoreSettings() : null,
            };
        }

        public static void Postfix(PscBuildingDestroyState __state)
        {
            // Free the destroyed standalone unit's policy so it doesn't linger in the static store for
            // the rest of the session (which would keep IsEmpty false, so the hot-path early-outs never
            // re-engage, and pin the orphaned settings object alive). Permanent only — minify uses
            // DeSpawn, not Destroy, so a minified pile keeps its limits to restore on reinstall.
            if (__state.ownSettings != null) PscStorageDataStore.Remove(__state.ownSettings);
            PscMapComponent.For(__state.map)?.PruneFeederLinksAndFlags();
        }
    }

    public sealed class PscBuildingDestroyState
    {
        public Map map;
        public StorageSettings ownSettings;
    }

    [HarmonyPatch(typeof(StorageGroupManager), nameof(StorageGroupManager.Notify_MemberRemoved))]
    public static class StorageGroupManager_NotifyMemberRemoved_FeederPatch
    {
        public static void Postfix(StorageGroupManager __instance)
        {
            PscMapComponent.For(__instance?.map)?.PruneFeederLinksAndFlags();
        }
    }
}
