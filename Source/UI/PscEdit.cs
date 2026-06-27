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

        // Create a new group from `defs` with the shared `lim` (in `mode`'s unit — items or packed
        // stacks). Pulls members out of any existing group, strips their per-def limits, assigns a letter,
        // and seeds refill. Returns the new group, or null if no valid members. A 1-member group is legal
        // (the ad-hoc "New group from this item" draft — grow it via the editor / right-click "Add to").
        // Seed ordering is load-bearing: g.limit is copied from `lim` HERE, before NormalizeGroups strips
        // the member's per-def entry, so a "New group" that seeds from an existing per-def cap never loses it.
        public static PscLimitGroup CreateGroup(StorageSettings settings, PscHaulUnit unit,
            IEnumerable<ThingDef> defs, PscDefLimit lim, string name = null,
            PscGroupCountMode mode = PscGroupCountMode.Stacks)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            var g = new PscLimitGroup { name = string.IsNullOrEmpty(name) ? null : name, countMode = mode };
            if (lim != null) { g.limit.Lower = lim.Lower; g.limit.Upper = lim.Upper; }
            foreach (var d in defs)
            {
                if (d == null) continue;
                var ex = data.GroupOf(d);
                if (ex != null && ex != g) { ex.members.Remove(d); ex.SyncNames(); }   // move into the new group
                if (!g.members.Contains(d)) g.members.Add(d);
                settings.filter.SetAllow(d, true);
            }
            if (g.members.Count < 1) return null;
            g.SyncNames();
            data.limitGroups.Add(g);
            data.NormalizeGroups();                 // assign letter, strip per-def limits on members, rebuild index
            data.Notify_GroupLimitSet(g, unit);
            PscMapComponent.NotifyPolicyChanged(settings);
            if (PscLog.Enabled) PscLog.Msg("group: created " + Describe(g));
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
            if (PscLog.Enabled) PscLog.Msg("group: added " + def.defName + " -> " + Describe(g));
        }

        // Remove `def` from its group (dissolving the group only if it drops to zero members; a 1-member
        // group is still a valid draft). The def is left UNALLOWED in the filter — "remove from group"
        // takes the item out of the storage entirely, rather than silently leaving it as plain allowed
        // storage. (ClearLimit handles the keep-allowed case via its own SetAllow.)
        public static void RemoveFromGroup(StorageSettings settings, ThingDef def)
        {
            var data = PscStorageDataStore.TryGet(settings);
            if (data == null || def == null) return;
            if (data.GroupOf(def) == null) return;          // not grouped: nothing to remove
            RemoveFromGroupInternal(data, def);
            settings.filter.SetAllow(def, false);           // leave the def unallowed (removed from storage)
            PscMapComponent.NotifyPolicyChanged(settings);
            if (PscLog.Enabled) PscLog.Msg("group: removed " + def.defName + " (now unallowed)");
        }

        private static void RemoveFromGroupInternal(PscStorageData data, ThingDef def)
        {
            var g = data.GroupOf(def);
            if (g == null) return;
            g.members.Remove(def);
            g.SyncNames();
            data.NormalizeGroups();                 // drops the group only when no members remain
        }

        // Dissolve a group; its members revert to no limit.
        public static void DissolveGroup(StorageSettings settings, PscLimitGroup g)
        {
            var data = PscStorageDataStore.TryGet(settings);
            if (data == null || g == null) return;
            if (PscLog.Enabled) PscLog.Msg("group: dissolved " + Describe(g));
            data.limitGroups.Remove(g);
            data.RebuildGroupIndex();
            PscMapComponent.NotifyPolicyChanged(settings);
        }

        // Set a group's shared limit (in `mode`'s unit — items or packed stacks), its count mode, and
        // re-seed its refill state. The editor calls this when the values OR the count mode change.
        public static void ApplyGroupLimit(StorageSettings settings, PscHaulUnit unit, PscLimitGroup g,
            PscDefLimit lim, PscGroupCountMode mode)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            if (g == null) return;
            g.limit.Lower = lim?.Lower;
            g.limit.Upper = lim?.Upper;
            g.countMode = mode;
            data.Notify_GroupLimitSet(g, unit);
            PscMapComponent.NotifyPolicyChanged(settings);
            if (PscLog.Enabled) PscLog.Msg("group: limit set " + Describe(g));
        }

        // One-line dev-log summary of a group: letter, optional name, count mode, limit, and members.
        // Only called under PscLog.Enabled, so the string build is paid only when diagnostics are on.
        private static string Describe(PscLimitGroup g)
        {
            if (g == null) return "<null group>";
            string lo = g.limit != null && g.limit.Lower.HasValue ? g.limit.Lower.Value.ToString() : "-";
            string hi = g.limit != null && g.limit.Upper.HasValue ? g.limit.Upper.Value.ToString() : "-";
            string members = g.memberNames != null ? string.Join(",", g.memberNames) : "";
            return $"[{g.letter}{(string.IsNullOrEmpty(g.name) ? "" : ":" + g.name)}] mode={g.countMode} "
                + $"limit={lo}..{hi} refill={(g.limit != null ? g.limit.refill.ToString() : "-")} "
                + $"members({g.members?.Count ?? 0})={members}";
        }
    }
}
