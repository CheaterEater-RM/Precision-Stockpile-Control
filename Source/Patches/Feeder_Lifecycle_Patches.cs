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
    // the feeder links, which are MapComponent-side. On join, the new group adopts the first member's
    // links; on unlink, the departing member keeps a copy of the group's links.
    [HarmonyPatch(typeof(StorageGroupUtility), nameof(StorageGroupUtility.SetStorageGroup))]
    public static class StorageGroupUtility_SetStorageGroup_FeederPatch
    {
        public static void Prefix(IStorageGroupMember member, out StorageGroup __state)
        {
            __state = member?.Group;   // old group (may be null)
        }

        public static void Postfix(IStorageGroupMember member, StorageGroup newGroup, StorageGroup __state)
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

            if (newGroup != null && __state == null)        // joined
            {
                if (newGroup.MemberCount <= 1)              // first member: group adopts its links
                    psc.Links.AdoptLinks(memberId, newGroup.GetUniqueLoadID());
                // Member now resolves to the group; drop its standalone edges (group holds them).
                psc.Links.RemoveAllFor(memberId);
            }
            else if (newGroup == null && __state != null)   // left: keep a copy of the group's links
            {
                psc.Links.AdoptLinks(__state.GetUniqueLoadID(), memberId);
            }
        }
    }
}
