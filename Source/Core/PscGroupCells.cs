using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Cell-aware enforcement for a Stacks-mode limit group (which counts OCCUPIED CELLS — "the stacks you
    // see"). The per-tile feature's recipe, applied to groups: at the group's cell cap, a NEW cell would
    // push the visible stack count past the cap, so steer intake into existing partial cells (top them
    // off) and never open a new one. Mirrors PscPerTile — cell-keyed, cheapest-first early-outs, one
    // O(1) thingGrid read at most. A colony with no Stacks-group pays only the IsEmpty/no-group checks.
    internal static class PscGroupCells
    {
        // Resolve a Stacks-mode, non-default, upper-capped limit group that covers `cell` and governs
        // `def`. False (no group) for every other case, so callers fall through to their normal path.
        private static bool TryResolve(IntVec3 cell, Map map, ThingDef def,
            out PscHaulUnit unit, out PscStorageData data, out PscLimitGroup g)
        {
            unit = default; data = null; g = null;
            if (def == null || map == null) return false;
            if (PscStorageDataStore.IsEmpty) return false;
            var psc = PscMapComponent.For(map);
            if (psc == null || !psc.anyGroupCellActive) return false;   // no cell-mode group on this map
            unit = PscHaulUnit.ResolveCell(cell, map);
            if (!unit.IsValid) return false;
            data = PscStorageDataStore.TryGet(unit.Settings);
            if (data == null) { unit = default; return false; }
            g = data.GroupOf(def);
            if (g == null || g.countMode != PscGroupCountMode.Stacks
                || g.limit == null || g.limit.IsDefault || !g.limit.Upper.HasValue)
            {
                unit = default; data = null; g = null;
                return false;
            }
            return true;
        }

        // NoStorageBlockersIn steer: at (or over) the cell cap, an EMPTY cell for `def` would be a NEW
        // occupied cell beyond the cap, so block it — the search then routes intake into existing partial
        // cells to top them off. Below cap, nothing is blocked (new cells allowed up to the cap). A cell
        // that already holds a same-def stack (a partial to merge into) is never blocked here.
        public static bool BlocksNewCellAtCap(IntVec3 cell, Map map, ThingDef def)
        {
            if (!TryResolve(cell, map, def, out var unit, out var data, out var g)) return false;
            if (data.GetGroupPhysicalStackCount(g, unit) < g.limit.Upper.Value) return false;  // below cap
            var existing = map.thingGrid.ThingAt(cell, def);
            return existing == null || existing.stackCount <= 0;                                 // empty -> new cell
        }

        // Drop / haul clamp: items of `def` that may land in THIS cell without opening a stack beyond the
        // group's cell cap. A partial of `def` here -> its fill room (stackLimit - existing), even at cap.
        // An empty cell below cap -> a full stack (new cell allowed). An empty cell at cap, or any cell
        // when the group is already OVER cap -> 0 (no intake; the over-cap drain trims the excess).
        // Returns false when no Stacks-group governs `def` here (caller uses its normal item-room path).
        public static bool TryGetCellFillRoom(IntVec3 cell, Map map, ThingDef def, out int room)
        {
            room = 0;
            if (!TryResolve(cell, map, def, out var unit, out var data, out var g)) return false;
            int cells = data.GetGroupPhysicalStackCount(g, unit);
            int cap = g.limit.Upper.Value;
            var existing = map.thingGrid.ThingAt(cell, def);
            int have = existing != null ? existing.stackCount : 0;
            if (cells > cap) { room = 0; return true; }                 // over cap: drain only
            if (have <= 0 && cells >= cap) { room = 0; return true; }   // new cell at cap: none
            room = Mathf.Max(0, Mathf.Max(1, def.stackLimit) - have);   // top off a partial / fill a new cell
            return true;
        }
    }
}
