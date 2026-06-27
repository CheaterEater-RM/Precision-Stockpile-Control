using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Single place that mutates a unit's policy for one def / one group + keeps the vanilla filter in
    // sync. Per-def callers batch several defs then call PscMapComponent.NotifyPolicyChanged once; the
    // group mutators are standalone menu actions and notify themselves.
    //
    // Group rule of thumb: a grouped def is governed by its GROUP, never by a per-def limit. So
    // ApplyLimit leaves a grouped def alone (the bulk "Apply to all" must not silently reset a group
    // via one member), ClearLimit removes a def FROM its group, and ClearAllLimits dissolves groups.
    // Editing a group's shared limit goes through ApplyGroupLimit (pooled, raw item totals).
    internal static class PscEdit
    {
        public static void ApplyLimit(StorageSettings settings, PscHaulUnit unit, ThingDef def, PscDefLimit lim)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            // Grouped def: the group governs it. Don't create a per-def limit or disturb the group; the
            // group editor (ApplyGroupLimit) is the way to change a grouped def's limit.
            if (data.GroupOf(def) != null) { settings.filter.SetAllow(def, true); return; }
            if (lim == null || lim.IsDefault) data.limits.Remove(def);
            else data.limits[def] = lim;
            settings.filter.SetAllow(def, true);
            data.Notify_LimitSet(def, unit);
        }

        public static void ClearLimit(StorageSettings settings, ThingDef def, bool allow)
        {
            var data = PscStorageDataStore.TryGet(settings);
            if (data != null && data.GroupOf(def) != null) RemoveFromGroupInternal(data, def);
            else data?.limits.Remove(def);
            settings.filter.SetAllow(def, allow);
        }

        public static void ClearAllLimits(StorageSettings settings)
        {
            var data = PscStorageDataStore.TryGet(settings);
            if (data == null || (data.limits.Count == 0 && !data.HasAnyGroup)) return;
            data.limits.Clear();
            data.limitGroups.Clear();
            data.RebuildGroupIndex();
            PscMapComponent.NotifyPolicyChanged(settings);
        }

        // ---- Limit groups ----

        // Create a new group from `defs` with the shared `lim` (raw item totals). Pulls members out of
        // any existing group, strips their per-def limits, assigns a letter, and seeds refill. Returns
        // the new group, or null if fewer than 2 valid members.
        public static PscLimitGroup CreateGroup(StorageSettings settings, PscHaulUnit unit,
            IEnumerable<ThingDef> defs, PscDefLimit lim, string name = null)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            var g = new PscLimitGroup { name = string.IsNullOrEmpty(name) ? null : name };
            if (lim != null) { g.limit.Lower = lim.Lower; g.limit.Upper = lim.Upper; }
            foreach (var d in defs)
            {
                if (d == null) continue;
                var ex = data.GroupOf(d);
                if (ex != null && ex != g) { ex.members.Remove(d); ex.SyncNames(); }   // move into the new group
                if (!g.members.Contains(d)) g.members.Add(d);
                settings.filter.SetAllow(d, true);
            }
            if (g.members.Count < 2) return null;
            g.SyncNames();
            data.limitGroups.Add(g);
            data.NormalizeGroups();                 // assign letter, strip per-def limits on members, rebuild index
            data.Notify_GroupLimitSet(g, unit);
            PscMapComponent.NotifyPolicyChanged(settings);
            return g;
        }

        // Add `def` to group `g`, moving it out of any other group and overriding (removing) its
        // individual per-def limit. The row label switches to the group letter — the override is visible.
        public static void AddToGroup(StorageSettings settings, PscHaulUnit unit, PscLimitGroup g, ThingDef def)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            if (g == null || def == null || !data.limitGroups.Contains(g)) return;
            var cur = data.GroupOf(def);
            if (cur == g) return;
            if (cur != null) { cur.members.Remove(def); cur.SyncNames(); }
            if (!g.members.Contains(def)) g.members.Add(def);
            g.SyncNames();
            settings.filter.SetAllow(def, true);
            data.limits.Remove(def);                // override the individual limit (shown via the row label)
            data.NormalizeGroups();
            data.Notify_GroupLimitSet(g, unit);
            PscMapComponent.NotifyPolicyChanged(settings);
        }

        // Remove `def` from its group (auto-dissolving the group if it drops below 2 members). The def
        // reverts to NO limit.
        public static void RemoveFromGroup(StorageSettings settings, ThingDef def)
        {
            var data = PscStorageDataStore.TryGet(settings);
            if (data == null) return;
            RemoveFromGroupInternal(data, def);
            PscMapComponent.NotifyPolicyChanged(settings);
        }

        private static void RemoveFromGroupInternal(PscStorageData data, ThingDef def)
        {
            var g = data.GroupOf(def);
            if (g == null) return;
            g.members.Remove(def);
            g.SyncNames();
            data.NormalizeGroups();                 // dissolves the group if < 2 members remain
        }

        // Dissolve a group; its members revert to no limit.
        public static void DissolveGroup(StorageSettings settings, PscLimitGroup g)
        {
            var data = PscStorageDataStore.TryGet(settings);
            if (data == null || g == null) return;
            data.limitGroups.Remove(g);
            data.RebuildGroupIndex();
            PscMapComponent.NotifyPolicyChanged(settings);
        }

        // Set a group's shared limit (raw item totals) and re-seed its refill state.
        public static void ApplyGroupLimit(StorageSettings settings, PscHaulUnit unit, PscLimitGroup g, PscDefLimit lim)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            if (g == null) return;
            g.limit.Lower = lim?.Lower;
            g.limit.Upper = lim?.Upper;
            data.Notify_GroupLimitSet(g, unit);
            PscMapComponent.NotifyPolicyChanged(settings);
        }
    }
}
