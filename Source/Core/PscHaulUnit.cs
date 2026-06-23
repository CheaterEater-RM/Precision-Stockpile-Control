using System;
using System.Collections.Concurrent;
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

        // Build-once id cache. GetUniqueLoadID() allocates a fresh string on every call, and this getter
        // is hit per feeder edge check (HasEdge) plus the live-id scans — so memoise by the canonical
        // group's REFERENCE identity (the id is constant for an object's lifetime). ConcurrentDictionary
        // with lock-free reads, NOT a plain Dictionary: AllowedToAccept — and thus this getter via the
        // feeder gate — can run on off-main reachability threads (see PscAdmissionScope), so a plain map
        // would race. Bounded against leaks by ClearIdCache() on new-game/load (PscGameComponent), every
        // feeder prune (PscFeederManager.PruneFeederLinksAndFlags), and map removal.
        private static readonly ConcurrentDictionary<object, string> idCache =
            new ConcurrentDictionary<object, string>(ReferenceComparer.Instance);

        // Stable string handle for an endpoint. Feeder links (D7) store endpoints by GetUniqueLoadID()
        // and resolve them lazily on load.
        public string UniqueLoadID
        {
            get
            {
                if (group == null) return null;
                if (idCache.TryGetValue(group, out var cached)) return cached;
                string id = null;
                if (group is StorageGroup sg) id = sg.GetUniqueLoadID();
                else if (group is SlotGroup slot && slot.parent is ILoadReferenceable lr) id = lr.GetUniqueLoadID();
                if (id != null) idCache[group] = id;
                return id;
            }
        }

        // Drop the id cache. Called from rebuildable-static lifecycle seams (new-game/load ctor, feeder
        // prune, map removal) so a dead group object never lingers as a key.
        public static void ClearIdCache() => idCache.Clear();

        // Reference-identity comparer for idCache, matching this struct's reference-equality Equals
        // (GetHashCode uses RuntimeHelpers.GetHashCode too). These vanilla group objects don't override
        // equality, but keying on identity removes any doubt.
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
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

        // Physical stack-slot capacity, floored by the current held-stack count so a caller never
        // treats the unit as smaller than what it already physically holds (deep-storage aware via
        // GetMaxItemsAllowedInCell). This is the same basis the limit editor's slider uses, so a
        // pasted limit clamped against it matches what the editor would have allowed. Returns false
        // (slots = 0) for a unit with no cells or on error, so callers can skip rather than clamp to
        // a bogus capacity. CellsList is a static temp: consumed immediately, never retained.
        public bool TryGetStackSlots(out int slots)
        {
            slots = 0;
            try
            {
                var cells = group?.CellsList;
                if (cells == null || cells.Count == 0) return false;
                var map = Map;
                int total = 0;
                for (int i = 0; i < cells.Count; i++)
                    total += map != null ? cells[i].GetMaxItemsAllowedInCell(map) : 1;

                var held = HeldThings;
                if (held != null)
                {
                    int heldStacks = 0;
                    foreach (var t in held)
                        if (t != null) heldStacks++;
                    if (heldStacks > total) total = heldStacks;
                }
                slots = Mathf.Max(1, total);
                return true;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[PSC] TryGetStackSlots failed: " + ex, 0x1C5A0005);
                slots = 0;
                return false;
            }
        }

        // Pawn-agnostic physical item space for `def` across the unit's cells, summed but capped at
        // `capAt` (early-out once reached, so a unit with plenty of room is cheap; a near-full unit pays a
        // full O(cells) scan). Deep-storage aware via vanilla GetItemStackSpaceLeftFor. Used by the batch
        // destination-room gate for UNCAPPED batched units (the capped case uses cap-room arithmetic). It
        // ignores IsGoodStoreCell's pawn reservation/reachability, so it OVER-estimates room vs a specific
        // pawn — the safe direction for a reject gate (never a false reject). CellsList is a static temp,
        // consumed immediately, never retained.
        public int PhysicalRoomForDef(ThingDef def, int capAt)
        {
            if (def == null) return 0;
            var cells = group?.CellsList;
            var map = Map;
            if (cells == null || map == null) return 0;
            int room = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                room += cells[i].GetItemStackSpaceLeftFor(map, def);
                if (room >= capAt) return room;
            }
            return room;
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

        // A representative on-map draw position for the feeder overlay and the alarm zoom target: the
        // geometric centre of the unit's bounding box, so a multi-cell unit points to its middle
        // rather than to one end cell (e.g. a two-cell shelf). Matches the box centre PscFeederLayout
        // spreads ports from, so a centroid-fallback route shares its origin with a ported one.
        // CellsList may be a shared temporary list (M3 §5 caveat) — iterated immediately, never retained.
        public bool TryGetDrawCenter(out Vector3 center)
        {
            center = default;
            var cells = group?.CellsList;
            if (cells == null || cells.Count == 0) return false;
            int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
            for (int i = 0; i < cells.Count; i++)
            {
                var c = cells[i];
                if (c.x < minX) minX = c.x;
                if (c.x > maxX) maxX = c.x;
                if (c.z < minZ) minZ = c.z;
                if (c.z > maxZ) maxZ = c.z;
            }
            center = new Vector3((minX + maxX + 1) / 2f, 0f, (minZ + maxZ + 1) / 2f);
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
