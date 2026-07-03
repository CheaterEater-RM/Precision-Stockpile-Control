using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // A subpriority-paint target: a storage whose fine-order letter can be written, whether a BUILT
    // unit (live slot group) or an unbuilt Blueprint_Storage / storage Frame. Transient, UI-only —
    // nothing here is scribed. Lets the paint tool seed from and paint onto blueprints exactly like
    // built storage; painting a blueprint writes its StorageSettings, which carries to the finished
    // shelf via StorageSettings.CopyFrom.
    //
    // PscHaulUnit only models live slot groups, so it cannot represent a blueprint (no slot group);
    // this struct is the paint tool's own resolution layer over both. PscHaulUnit.ResolveCell is left
    // unchanged (used additively as the built-first probe), so the feeder designator is unaffected.
    public readonly struct PscPaintTarget
    {
        public readonly StorageSettings Settings;
        public readonly string Id;              // dedup key: live UniqueLoadID or Thing.GetUniqueLoadID
        public readonly Map Map;
        private readonly PscHaulUnit unit;      // valid for a built target; default for unbuilt
        private readonly Thing thing;           // the blueprint/frame for an unbuilt target; null for built

        private PscPaintTarget(PscHaulUnit unit)
        {
            this.unit = unit;
            thing = null;
            Settings = unit.Settings;
            Id = unit.UniqueLoadID;
            Map = unit.Map;
        }

        private PscPaintTarget(Thing thing, StorageSettings settings)
        {
            unit = default;
            this.thing = thing;
            Settings = settings;
            Id = thing.GetUniqueLoadID();
            Map = thing.Map;
        }

        public bool IsValid => Settings != null && Map != null;

        // Occupied cells for the session highlights: the live slot group's cells (built) or the
        // blueprint/frame footprint (unbuilt). For a built unit this is CellsList (a shared static
        // temp) — the caller passes it straight to DrawFieldEdges and never retains it.
        public List<IntVec3> Cells
            => thing != null ? thing.OccupiedRect().Cells.ToList() : unit.group?.CellsList;

        public bool SameAs(in PscPaintTarget other) => Id != null && Id == other.Id;

        // Resolve a cell to a paintable storage: a live unit first (built), else an unbuilt
        // Blueprint_Storage / storage Frame occupying the cell.
        public static bool TryResolveCell(IntVec3 c, Map map, out PscPaintTarget target)
        {
            var u = PscHaulUnit.ResolveCell(c, map);
            if (u.IsValid) { target = new PscPaintTarget(u); return true; }

            var things = c.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                if ((things[i] is Blueprint_Storage || things[i] is Frame)
                    && things[i] is IStoreSettingsParent p)
                {
                    var s = p.GetStoreSettings();
                    if (s != null) { target = new PscPaintTarget(things[i], s); return true; }
                }
            }
            target = default;
            return false;
        }

        // The seed target for the gizmo's launching storage — built (live unit) or unbuilt (the
        // blueprint/frame Thing behind the parent).
        public static bool TryFromParent(IStoreSettingsParent parent, StorageSettings settings,
            PscHaulUnit unit, out PscPaintTarget target)
        {
            if (unit.IsValid) { target = new PscPaintTarget(unit); return target.IsValid; }
            if (parent is Thing t && settings != null) { target = new PscPaintTarget(t, settings); return target.IsValid; }
            target = default;
            return false;
        }
    }
}
