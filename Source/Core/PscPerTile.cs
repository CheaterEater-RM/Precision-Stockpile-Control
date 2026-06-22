using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Per-cell ("per-tile") item cap for FLOOR stockpiles only (the niche "spread items thin" feature,
    // opt-in behind PscSettings.perTileLimits, default off). Shared room/cap calculator, the per-tile
    // analogue of PscCap.TryGetRoom.
    //
    // Floor-only by design: a per-tile cap never applies to shelves / Building_Storage / deep storage /
    // linked StorageGroups. A floor Zone_Stockpile never joins a StorageGroup, so its SlotGroup owns its
    // own StorageSettings and that is the key in PscStorageDataStore.
    //
    // Every entry early-outs cheapest-first: store empty -> master toggle off -> no per-tile unit on this
    // map (anyPerTileActive) -> not a floor cell -> no cap, with no LINQ/allocation and one O(1) thingGrid
    // read at most. A colony with the feature off pays two field reads (IsEmpty + the setting bool).
    internal static class PscPerTile
    {
        // The per-tile cap covering `cell` (max items of any one def on that floor cell), or false when no
        // per-tile cap applies (caller runs vanilla untouched).
        public static bool TryGetCellCap(IntVec3 cell, Map map, out int cap)
        {
            cap = 0;
            if (map == null) return false;
            if (PscStorageDataStore.IsEmpty) return false;
            if (PscMod.Settings == null || !PscMod.Settings.perTileLimits) return false;
            var psc = PscMapComponent.For(map);
            if (psc == null || !psc.anyPerTileActive) return false;
            var slot = map.haulDestinationManager?.SlotGroupAt(cell);
            if (slot == null || !(slot.parent is Zone_Stockpile)) return false;   // floor stockpiles only
            var data = PscStorageDataStore.TryGet(slot.Settings);
            if (data == null || data.perTileLimit <= 0) return false;
            cap = data.perTileLimit;
            return true;
        }

        // The per-tile cap for a SPAWNED thing's current cell (carried / unspawned / loose -> false).
        public static bool TryGetCellCap(Thing t, out int cap)
        {
            cap = 0;
            if (t == null || !t.Spawned) return false;
            return TryGetCellCap(t.Position, t.Map, out cap);
        }

        // Remaining room for `def` on floor cell `cell` under its per-tile cap (cap - existing same-def
        // stack on that cell). False when no per-tile cap applies. `room` is clamped >= 0.
        public static bool TryGetCellRoom(IntVec3 cell, Map map, ThingDef def, out int room)
        {
            room = 0;
            if (def == null) return false;
            if (!TryGetCellCap(cell, map, out int cap)) return false;
            var existing = map.thingGrid.ThingAt(cell, def);
            int have = existing != null ? existing.stackCount : 0;
            room = cap - have;
            if (room < 0) room = 0;
            return true;
        }
    }
}
