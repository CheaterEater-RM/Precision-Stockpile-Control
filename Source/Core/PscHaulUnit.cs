using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // The canonical "haul unit": a StorageGroup when storages are linked, otherwise a standalone
    // SlotGroup. Both implement ISlotGroup, which exposes the active Settings, HeldThings and
    // CellsList. This is the one place that resolves vanilla storage objects to PSC units — never
    // scatter this logic across patches (design §5).
    //
    // NOTE: ISlotGroup.CellsList / StorageGroup.CellsList return a STATIC temporary list. Never
    // retain it; iterate HeldThings (which reads the grid directly) for counting instead.
    public readonly struct PscHaulUnit : IEquatable<PscHaulUnit>
    {
        public readonly ISlotGroup group;   // canonical: StorageGroup, or a standalone SlotGroup

        public PscHaulUnit(ISlotGroup canonicalGroup) { group = canonicalGroup; }

        public bool IsValid => group != null;
        public StorageSettings Settings => group?.Settings;
        public IEnumerable<Thing> HeldThings => group?.HeldThings;

        public Map Map
        {
            get
            {
                if (group is StorageGroup sg) return sg.Map;
                if (group is SlotGroup slot) return slot.parent?.Map;
                return null;
            }
        }

        // Stable string handle for an endpoint. Groundwork for M3 feeder links (D7), which store
        // endpoints by GetUniqueLoadID() and resolve them lazily on load.
        public string UniqueLoadID
        {
            get
            {
                if (group is StorageGroup sg) return sg.GetUniqueLoadID();
                if (group is SlotGroup slot && slot.parent is ILoadReferenceable lr) return lr.GetUniqueLoadID();
                return null;
            }
        }

        // User-facing name for alarm messages / dialog titles. A StorageGroup uses its renamable
        // label; a standalone storage uses its parent's slot-yielder label (the zone label for a
        // stockpile, the building LabelCap for a shelf).
        public string Label
        {
            get
            {
                if (group is StorageGroup sg) return sg.RenamableLabel;
                if (group is SlotGroup slot && slot.parent != null) return slot.parent.SlotYielderLabel();
                return null;
            }
        }

        // Whole-unit fullness as occupied stack-slots / total stack-slots, rounded to 0-100. Slots,
        // not items (deep-storage aware via GetMaxItemsAllowedInCell): robust for mixed-def piles and
        // answers "how much room is left". Returns false for a unit with no cells. CellsList is a
        // static temp — fully consumed into totalSlots before HeldThings is touched, never retained.
        public bool TryGetFullnessPct(out int pct)
        {
            pct = 0;
            var cells = group?.CellsList;
            if (cells == null || cells.Count == 0) return false;
            var map = Map;
            int totalSlots = 0;
            for (int i = 0; i < cells.Count; i++)
                totalSlots += map != null ? cells[i].GetMaxItemsAllowedInCell(map) : 1;
            if (totalSlots < cells.Count) totalSlots = cells.Count;
            if (totalSlots <= 0) return false;

            int occupied = 0;
            var held = HeldThings;
            if (held != null)
                foreach (var t in held)
                    if (t != null) occupied++;
            if (occupied > totalSlots) occupied = totalSlots;

            pct = Mathf.RoundToInt(100f * occupied / totalSlots);
            return true;
        }

        private static ISlotGroup Canonicalize(ISlotGroup slot)
        {
            if (slot == null) return null;
            return slot.StorageGroup != null ? (ISlotGroup)slot.StorageGroup : slot;
        }

        // A raw slot group -> its canonical haul unit. Used when enumerating every storage on a map
        // (overlay draw, live-id pruning) where we already hold the SlotGroup.
        public static PscHaulUnit FromSlotGroup(ISlotGroup slot)
        {
            var canon = Canonicalize(slot);
            return canon == null ? default : new PscHaulUnit(canon);
        }

        // A representative on-map draw position for the feeder overlay: the centroid of the unit's
        // cells snapped to the nearest actual cell so the marker sits on storage. CellsList may be a
        // shared temporary list (M3 §5 caveat) — iterated immediately, never retained.
        public bool TryGetDrawCenter(out Vector3 center)
        {
            center = default;
            var cells = group?.CellsList;
            if (cells == null || cells.Count == 0) return false;
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < cells.Count; i++) sum += cells[i].ToVector3Shifted();
            Vector3 avg = sum / cells.Count;
            IntVec3 best = cells[0];
            float bestDist = float.MaxValue;
            for (int i = 0; i < cells.Count; i++)
            {
                float d = (cells[i].ToVector3Shifted() - avg).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = cells[i]; }
            }
            center = best.ToVector3Shifted();
            return true;
        }

        // settings.owner -> StorageGroup => the group; ISlotGroupParent member => its group if
        // linked, else its slot group; null/fixed/other => no PSC unit (out of scope).
        public static PscHaulUnit ResolveSettings(StorageSettings settings)
        {
            var owner = settings?.owner;
            if (owner == null) return default;
            if (owner is StorageGroup sg) return new PscHaulUnit(sg);
            if (owner is ISlotGroupParent sgp)
            {
                var slot = sgp.GetSlotGroup();
                return slot == null ? default : new PscHaulUnit(Canonicalize(slot));
            }
            return default;
        }

        // A spawned thing -> its current storage unit. Carried/unspawned/loose => invalid (treated
        // as having no source link by feeder admission).
        public static PscHaulUnit ResolveCurrent(Thing t)
        {
            if (t == null || !t.Spawned) return default;
            var slot = t.GetSlotGroup();
            return slot == null ? default : new PscHaulUnit(Canonicalize(slot));
        }

        // A map cell -> the storage unit covering it (canonical). Used by the M2 drop/capacity
        // hard-cap seams which work in cell terms.
        public static PscHaulUnit ResolveCell(IntVec3 cell, Map map)
        {
            var slot = map?.haulDestinationManager?.SlotGroupAt(cell);
            return slot == null ? default : new PscHaulUnit(Canonicalize(slot));
        }

        public bool Equals(PscHaulUnit other) => ReferenceEquals(group, other.group);
        public override bool Equals(object obj) => obj is PscHaulUnit u && Equals(u);
        public override int GetHashCode() => group == null ? 0 : RuntimeHelpers.GetHashCode(group);
    }
}
