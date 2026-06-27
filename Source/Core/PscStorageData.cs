using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Refill hysteresis edge state, PERSISTED on the limit it describes (D15). Meaningful only in the open
    // band lower<count<upper; on the rails it is re-derived from the count. Stored by Scribe_Values, which
    // writes the NAME — reorder-safe, but NEVER rename a value (save-compat surface). Default Unset writes
    // nothing (write-nothing-when-empty), and means "never evaluated" → admission treats it as fill-up on
    // first evaluation, so a brand-new or pre-field limit fills to upper before hysteresis takes over.
    public enum PscRefillState : byte
    {
        Unset = 0,       // never evaluated (or no lower threshold) — default to filling
        Refilling = 1,   // currently below the refill window — admission accepts intake
        Satisfied = 2,   // reached upper — admission blocks intake until count drops to lower
    }

    // Per-def limit policy. Limits are tracked in ITEMS, never stacks (stacks are a display-only
    // convenience). Stored as int sentinels (-1 = unset) rather than nullable ints to keep
    // Scribe_Values round-tripping boringly safe across versions.
    public class PscDefLimit : IExposable
    {
        public int lowerRaw = -1;   // -1 = unset (D15: "always refill"); 0 = only when empty; N = refill at <= N
        public int upperRaw = -1;   // -1 = unset (unlimited); N = the maximum (hard cap at drop time)

        // Hysteresis edge state, persisted (rides this limit's Deep round-trip, so it self-prunes with the
        // entry and can never desync from lower/upper). Runtime-derived; NOT part of IsDefault.
        public PscRefillState refill = PscRefillState.Unset;

        public int? Lower
        {
            get => lowerRaw < 0 ? (int?)null : lowerRaw;
            set => lowerRaw = value ?? -1;
        }

        public int? Upper
        {
            get => upperRaw < 0 ? (int?)null : upperRaw;
            set => upperRaw = value ?? -1;
        }

        public bool IsDefault => lowerRaw < 0 && upperRaw < 0;

        // Clone resets refill to Unset: a copy/paste is a FRESH policy decision, not a restore, so it must
        // not inherit the source pile's band state (the paste path re-seeds it via Notify_LimitsSeeded).
        public PscDefLimit Clone() => new PscDefLimit { lowerRaw = lowerRaw, upperRaw = upperRaw };

        // The lower/upper clamp invariant, shared by the limit editor (slider + text fields) and the
        // paste-time capacity clamp so it lives in exactly one place: lower in [0,max], upper in
        // [1,max], and lower never above upper.
        public static void ClampPair(ref int? lower, ref int? upper, int max)
        {
            if (lower.HasValue) lower = Mathf.Clamp(lower.Value, 0, max);
            if (upper.HasValue) upper = Mathf.Clamp(upper.Value, 1, max);
            if (lower.HasValue && upper.HasValue && lower.Value > upper.Value) lower = upper;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref lowerRaw, "lower", -1);
            Scribe_Values.Look(ref upperRaw, "upper", -1);
            Scribe_Values.Look(ref refill, "refill", PscRefillState.Unset);
        }
    }

    // Per-storage hauling mode (M5.2, Flickable-style). Normal = pure vanilla. Off / AcceptOnly
    // freeze contents (virtual-forbidden, never touches the real flag); Off / RetrieveOnly block
    // haul-in (admission). Stored by Scribe_Values, which writes the NAME — reorder-safe, but never
    // rename a value (save-compat surface, AGENTS.md).
    public enum PscStorageMode : byte
    {
        Normal = 0,        // storage on — vanilla behaviour
        Off = 1,           // no haul in, no haul out / use (contents frozen in place)
        AcceptOnly = 2,    // haul in allowed; no haul out / use (pile fills, never drains)
        RetrieveOnly = 3,  // haul out / use allowed; no haul in (pile drains, never refills)
    }

    // Which slice of policy a scoped right-click "Paste" applies. A widening ladder, so CopyScopedFrom
    // can gate fields with >=. The vanilla Priority band and PSC fine-order (subTier/letter) are NEVER
    // copied by any of these — only the unscoped left-click "everything" paste (vanilla CopyFrom path)
    // carries priorities. Save-irrelevant (UI selection only; never scribed).
    public enum PscPasteScope : byte
    {
        ItemsLimits = 0,              // per-def limits only (+ the vanilla allow/disallow filter, applied by the caller)
        ItemsLimitsRoutes = 1,        // + feeder routes (applied by the caller) + the pull/push flags
        EverythingButPriorities = 2,  // + batch / batchEmpty / mode / alarm
    }

    // PSC policy + runtime count cache attached to one vanilla StorageSettings object (keyed in
    // PscStorageDataStore). A StorageGroup shares one StorageSettings across its linked members,
    // so keying here shares PSC policy and counts across a linked group automatically.
    //
    // Count-cache strategy: drift seams mark (def) dirty; the count is recomputed lazily from the
    // unit's HeldThings on the next read and cached until dirtied again. This avoids fragile +/-
    // delta arithmetic — the central correctness risk for limit mods.
    public class PscStorageData : IExposable
    {
        // ---- Persistent policy ----
        // Invariant: non-null. Set at construction, re-created by CopyPolicyFrom, and restored on load
        // by the PostLoadInit guard in ExposeData. Runtime reads therefore skip container null-checks.
        public Dictionary<ThingDef, PscDefLimit> limits = new Dictionary<ThingDef, PscDefLimit>();
        public int batch;                 // batch fill: 0 = off, never bring fewer than N in one trip
        public int batchEmpty;            // batch empty: 0 = off, never remove fewer than N out in one trip
        public bool onlyFromSource;       // feeder: accept only items from a linked source unit
        public bool onlyToDestinations;   // feeder: send contents only to a linked destination unit
        public byte subTier;              // fine-order 1-10 sub-tier: 0 = unset
        public string letter;             // fine-order a-z subpriority: null/empty = none
        public PscStorageMode mode;       // hauling mode (Normal = vanilla)
        public int perTileLimit;          // per-cell cap for FLOOR stockpiles (opt-in, PscSettings.perTileLimits): 0 = off, else max items on one floor cell

        // Stockpile alarm (opt-in): null when unused, so a unit with no alarm writes nothing and
        // costs nothing. Holds the high/low fullness thresholds, dwell, cadence, notify style, and
        // its own persisted edge state. Shared across a linked StorageGroup like the rest of policy.
        public PscAlarmConfig alarm;

        // Limit groups (opt-in): each bundles several defs under ONE shared limit governing their
        // COMBINED total. A def is in at most one group, and a grouped def is NOT also in `limits`
        // (joining strips its per-def entry — the documented "override"). Empty list writes nothing.
        public List<PscLimitGroup> limitGroups = new List<PscLimitGroup>();

        // ---- Runtime cache (never scribed; rebuilt from HeldThings) ----
        // Hysteresis state is NOT here anymore — it is persisted on each PscDefLimit (lim.refill).
        private readonly Dictionary<ThingDef, int> counts = new Dictionary<ThingDef, int>();
        // Physical occupied-CELL count per def (the number of Thing instances of that def in the unit =
        // occupied cells in vanilla), populated in the SAME HeldThings walk as `counts` so it costs one
        // extra dict op per Thing. This IS the enforced unit for a Stacks-mode limit group ("count the
        // stacks you see") and also feeds the editor read-out / dev logs.
        private readonly Dictionary<ThingDef, int> physicalStackCounts = new Dictionary<ThingDef, int>();
        private readonly HashSet<ThingDef> dirtyDefs = new HashSet<ThingDef>();
        private bool allDirty = true;

        // Reservation-aware fill counting (opt-in, on by default). Per-def items committed to haul IN
        // but not yet delivered. NEVER scribed; this is the "fast delta" half of the split counter —
        // physical `counts` stays the source of truth, and PscMapComponent's periodic rebuild-from-
        // active-jobs corrects any drift. Only the soft planning gates read counts+reservedInbound
        // (GetEffectiveCount); the hard drop cap, over-cap drain, and freeze reads stay on physical.
        private readonly Dictionary<ThingDef, int> reservedInbound = new Dictionary<ThingDef, int>();

        // Runtime reverse index def -> its limit group (never scribed; rebuilt by RebuildGroupIndex on
        // load and after every group edit). A real, always-non-null field so the resolver's hot-path
        // miss is a single TryGetValue with no allocate-on-demand.
        private readonly Dictionary<ThingDef, PscLimitGroup> defToGroup = new Dictionary<ThingDef, PscLimitGroup>();

        public bool HasPersistentPolicy =>
            limits.Count > 0
            // Any group (>= 1 member) keeps the unit tracked AND saved — even an unconfigured default-limit
            // 1-member draft, so a freshly created group survives until configured rather than being dropped
            // by UpdateTracking's "no policy -> Remove" path. HasAnyLimit / restrictedDefs still gate on a
            // NON-default group, so an unconfigured group tracks but enforces nothing.
            || (limitGroups != null && limitGroups.Count > 0)
            || batch > 0 || batchEmpty > 0 || onlyFromSource || onlyToDestinations
            || subTier != 0 || !string.IsNullOrEmpty(letter)
            || mode != PscStorageMode.Normal
            || perTileLimit > 0
            || (alarm != null && alarm.IsActive);

        // True when any non-default limit group exists. Manual loop (no LINQ); the limitGroups list can
        // briefly hold default entries before a normalize pass prunes them.
        public bool HasAnyGroup
        {
            get
            {
                if (limitGroups == null) return false;
                for (int i = 0; i < limitGroups.Count; i++)
                {
                    var g = limitGroups[i];
                    if (g != null && g.limit != null && !g.limit.IsDefault) return true;
                }
                return false;
            }
        }

        // True when any non-default group counts in Stacks (occupied-cell) mode. Gates the cell-aware
        // group seam (PscGroupCells) so a colony with no cell-mode group skips it on the store-search path.
        public bool HasAnyStacksGroup
        {
            get
            {
                if (limitGroups == null) return false;
                for (int i = 0; i < limitGroups.Count; i++)
                {
                    var g = limitGroups[i];
                    if (g != null && g.countMode == PscGroupCountMode.Stacks
                        && g.limit != null && !g.limit.IsDefault) return true;
                }
                return false;
            }
        }

        // Per-def limit accessors. The admission, hard-cap, and refill paths gate on HasLimit then
        // read GetLimit; the filter-row / per-item editor UI uses the same pair. Limits are always
        // explicit per-def entries (whole-stockpile imports are expanded to per-def on migration).
        public bool HasLimit(ThingDef def)
        {
            return def != null && limits.TryGetValue(def, out var l) && l != null && !l.IsDefault;
        }

        public PscDefLimit GetLimit(ThingDef def)
        {
            return (def != null && limits.TryGetValue(def, out var l)) ? l : null;
        }

        // True when any non-default per-def limit is set. Manual loop (no LINQ) since the `limits`
        // dict can hold IsDefault entries. Used by the on-map overlay's "limits active" status icon.
        public bool HasAnyLimit
        {
            get
            {
                foreach (var kv in limits)
                    if (kv.Value != null && !kv.Value.IsDefault) return true;
                return HasAnyGroup;
            }
        }

        // True when any per-def limit has an UPPER cap (a maximum). Gates reservation-aware fill counting:
        // reserved-inbound is only ever recorded for upper-capped defs, so a colony with only refill
        // thresholds (lower, no upper) pays nothing.
        public bool HasAnyUpperLimit
        {
            get
            {
                foreach (var kv in limits)
                    if (kv.Value != null && !kv.Value.IsDefault && kv.Value.Upper.HasValue) return true;
                if (limitGroups != null)
                    for (int i = 0; i < limitGroups.Count; i++)
                    {
                        var g = limitGroups[i];
                        if (g != null && g.limit != null && !g.limit.IsDefault && g.limit.Upper.HasValue) return true;
                    }
                return false;
            }
        }

        // ---- Limit groups: reverse-index management + the effective-limit resolver ----
        // The resolver is the single seam that makes every enforcement site group-aware without
        // rewriting each one: a grouped def reports its GROUP's limit and the GROUP-SUM count; an
        // ungrouped def falls through to the exact per-def behaviour. The hot-path miss is one
        // TryGetValue, allocation-free.

        // The group `def` belongs to, or null. O(1).
        public PscLimitGroup GroupOf(ThingDef def)
            => def != null && defToGroup.TryGetValue(def, out var g) ? g : null;

        // Effective "has a limit": grouped (non-default shared limit) OR an individual per-def limit.
        public bool HasEffectiveLimit(ThingDef def)
        {
            if (def == null) return false;
            if (defToGroup.TryGetValue(def, out var g)) return g.limit != null && !g.limit.IsDefault;
            return HasLimit(def);
        }

        // The limit that governs `def`: the group's shared limit if grouped, else the per-def limit.
        public PscDefLimit GetEffectiveLimit(ThingDef def)
        {
            if (def != null && defToGroup.TryGetValue(def, out var g)) return g.limit;
            return GetLimit(def);
        }

        // Refill state that governs `def`: the group's shared refill if grouped, else the per-def one.
        public bool IsRefillingEffective(ThingDef def)
        {
            if (def != null && defToGroup.TryGetValue(def, out var g))
                return g.limit != null && g.limit.refill == PscRefillState.Refilling;
            return IsRefilling(def);
        }

        // Rebuild defToGroup from limitGroups: resolve each group's members, enforce "at most one group
        // per def" (first claim wins; later/duplicate claims dropped), and strip every grouped def from
        // the per-def `limits` dict (a grouped def is never also individually limited). Cheap; called on
        // load and after every group edit, never on the hot path.
        public void RebuildGroupIndex()
        {
            defToGroup.Clear();
            if (limitGroups == null) { limitGroups = new List<PscLimitGroup>(); return; }
            for (int gi = 0; gi < limitGroups.Count; gi++)
            {
                var g = limitGroups[gi];
                if (g == null) continue;
                g.ResolveMembers();
                var kept = new List<ThingDef>(g.members.Count);
                for (int i = 0; i < g.members.Count; i++)
                {
                    var d = g.members[i];
                    if (d == null || defToGroup.ContainsKey(d)) continue;   // missing, dup, or cross-group
                    defToGroup[d] = g;
                    limits.Remove(d);                                       // group overrides per-def
                    kept.Add(d);
                }
                g.members = kept;
                g.SyncNames();
            }
        }

        // Full normalize: rebuild the index, drop only fully-empty groups (a 1-member group is a legal
        // ad-hoc "named limit" draft and keeps its shared limit on the group), assign/dedupe letters, then
        // rebuild once more since membership/limits may have changed. Called from PostLoadInit and after
        // any membership mutation.
        public void NormalizeGroups()
        {
            RebuildGroupIndex();
            PruneDegenerateGroups();
            AssignGroupLetters();
            RebuildGroupIndex();
            MarkAllDirty();
        }

        private void PruneDegenerateGroups()
        {
            if (limitGroups == null) return;
            for (int i = limitGroups.Count - 1; i >= 0; i--)
            {
                var g = limitGroups[i];
                // Drop only null or empty groups. 1-member groups are allowed (ad-hoc drafts and the
                // single-survivor-after-content-mod-removal case alike keep the group + its shared cap).
                if (g == null || g.members.Count == 0) limitGroups.RemoveAt(i);
            }
        }

        // Ensure every group has a unique letter, keeping valid existing ones and reassigning blanks /
        // collisions deterministically by list order (so a corrupt save heals predictably).
        private void AssignGroupLetters()
        {
            if (limitGroups == null) return;
            var used = new HashSet<string>();
            for (int i = 0; i < limitGroups.Count; i++)
            {
                var g = limitGroups[i];
                if (g == null) continue;
                if (!string.IsNullOrEmpty(g.letter) && used.Add(g.letter)) continue;
                g.letter = NextFreeLetter(used);
                used.Add(g.letter);
            }
        }

        // Lowest free A..Z, then two-char AA, AB, … past 26 groups in one unit.
        private static string NextFreeLetter(HashSet<string> used)
        {
            for (char c = 'A'; c <= 'Z'; c++)
            {
                string s = c.ToString();
                if (!used.Contains(s)) return s;
            }
            for (char a = 'A'; a <= 'Z'; a++)
                for (char b = 'A'; b <= 'Z'; b++)
                {
                    string s = string.Concat(a, b);
                    if (!used.Contains(s)) return s;
                }
            return "?";
        }

        // ---- Dirty marking (cheap; called from drift-seam patches) ----
        public void MarkDirty(ThingDef def) { if (def != null) dirtyDefs.Add(def); }
        public void MarkAllDirty() { allDirty = true; rankCacheGen = -1; }

        // Cached fine-order rank (PscOrder.ComputeRankWithinBand). Runtime-only, never scribed. Recompute
        // only when the band changes — the band self-compare also transparently catches a vanilla
        // priority-button edit that has no PSC seam — or the global fine-order generation bumps. Any policy
        // edit (subTier/letter included) invalidates via MarkAllDirty resetting rankCacheGen above. Plain
        // ints keep the read branch tiny; -1 generation forces a recompute on first read / after a fresh
        // CopyPolicyFrom.
        // Thread-safety: GetRank is only reachable off-main under a hypothetical threading caller (vanilla 1.6
        // runs the search main-thread; see PHASE4 §6.1). Even then the unsynchronised write is a benign,
        // self-correcting race — every thread computes the IDENTICAL value for the same (subTier, letter, band,
        // gen), and settings.Priority is stable across a scan, so the three fields never combine into a
        // wrong-band rank; ints/byte writes are atomic, so no structural corruption is possible. This
        // benign-race property is SPECIFIC to these atomic int fields: it does NOT extend to the counts /
        // reservedInbound Dictionaries (structural Clear/Add), which are main-thread-only and not hardened.
        private int rankCache;
        private int rankCacheGen = -1;                                    // -1 forces a recompute on first read
        private StoragePriority rankCacheBand = (StoragePriority)byte.MaxValue;   // impossible-band sentinel

        public int GetRank(StoragePriority band)
        {
            if (rankCacheGen == PscOrder.FineOrderGeneration && rankCacheBand == band) return rankCache;
            rankCache = PscOrder.ComputeRankWithinBand(subTier, letter, band);
            rankCacheGen = PscOrder.FineOrderGeneration;
            rankCacheBand = band;
            return rankCache;
        }

        // ---- Count access (recomputes from the unit's HeldThings when dirty) ----
        public int GetCount(ThingDef def, PscHaulUnit unit)
        {
            if (allDirty) RecomputeAll(unit);
            else if (dirtyDefs.Contains(def)) RecomputeDef(def, unit);
            return counts.TryGetValue(def, out var c) ? c : 0;
        }

        public bool IsRefilling(ThingDef def)
        {
            var lim = GetLimit(def);
            return lim != null && lim.refill == PscRefillState.Refilling;
        }

        // Effective count = physical + reserved-inbound, read ONLY by the soft planning gates (admission
        // upper gate, batch-room gate, haul-job room clamp, PUAH/HD capacity probes). When the feature
        // is off it is byte-identical to GetCount. Invariant: effective >= physical (reservedInbound is
        // clamped >= 0), so admission only ever tightens relative to physical and never conflicts with
        // the physical-only hard cap / over-cap drain.
        public int GetEffectiveCount(ThingDef def, PscHaulUnit unit)
        {
            int n = GetCount(def, unit);
            if (!PscMod.Settings.reservedFillCounting) return n;
            return n + (reservedInbound.TryGetValue(def, out var r) ? r : 0);
        }

        // Group-aware physical count: the GROUP-SUM (Σ members) if `def` is grouped, else the per-def
        // count. Effective = physical + reserved-inbound, summed the same way (reservation stays keyed
        // per member def, so it rolls up naturally and the effective >= physical invariant holds).
        public int GetGroupAwareCount(ThingDef def, PscHaulUnit unit)
        {
            if (def != null && defToGroup.TryGetValue(def, out var g)) return GroupCount(g, unit);
            return GetCount(def, unit);
        }

        public int GetGroupAwareEffectiveCount(ThingDef def, PscHaulUnit unit)
        {
            if (def != null && defToGroup.TryGetValue(def, out var g)) return GroupEffectiveCount(g, unit);
            return GetEffectiveCount(def, unit);
        }

        // Physical occupied-cell count for `def` (number of Thing instances in the unit = occupied cells
        // in vanilla). In Stacks mode this IS the enforced group unit ("stacks you see"); also feeds the
        // editor read-out / dev logs. Recompute-safe (same lazy path as GetCount).
        public int GetPhysicalStackCount(ThingDef def, PscHaulUnit unit)
        {
            if (def == null) return 0;
            if (allDirty) RecomputeAll(unit);
            else if (dirtyDefs.Contains(def)) RecomputeDef(def, unit);
            return physicalStackCounts.TryGetValue(def, out var c) ? c : 0;
        }

        // A group's combined PHYSICAL occupied-cell count (Σ member occupied cells). In Stacks mode this
        // is the enforced group count; also feeds the editor read-out and dev logs.
        public int GetGroupPhysicalStackCount(PscLimitGroup g, PscHaulUnit unit)
        {
            if (g == null) return 0;
            int sum = 0;
            var ms = g.members;
            for (int i = 0; i < ms.Count; i++)
                if (ms[i] != null) sum += GetPhysicalStackCount(ms[i], unit);
            return sum;
        }

        // Public read of a group's enforced (mode-aware) combined count — occupied CELLS in Stacks mode
        // (the player's "stacks you see"), item sum in Items mode. Used by the editor read-out and the
        // decision-time diagnostic log.
        public int GroupEnforcedCount(PscLimitGroup g, PscHaulUnit unit) => GroupCount(g, unit);

        // True when `def` has slack in an EXISTING cell it occupies (a partial that can be topped off
        // without opening a new cell): its cells*stackLimit exceeds its item total. Recompute-safe.
        public bool GroupDefHasMergeRoom(ThingDef def, PscHaulUnit unit)
        {
            if (def == null) return false;
            int cells = GetPhysicalStackCount(def, unit);
            if (cells <= 0) return false;
            return cells * Mathf.Max(1, def.stackLimit) > GetCount(def, unit);
        }

        // Any member of `g` has merge room (a toppable partial cell). Recompute-safe; used by the edit-time
        // refill seeder and the admission "at cap but still toppable" gate.
        public bool GroupAnyMergeRoom(PscLimitGroup g, PscHaulUnit unit)
        {
            if (g == null) return false;
            var ms = g.members;
            for (int i = 0; i < ms.Count; i++)
                if (ms[i] != null && GroupDefHasMergeRoom(ms[i], unit)) return true;
            return false;
        }

        // Group's combined count in its OWN unit: occupied CELLS (Stacks mode) or item sum (Items mode).
        // Public: may recompute, so it is only ever called from enforcement / room / edit-seed paths,
        // never from a recompute (the refill recompute uses the *FromCounts helpers, which don't re-enter).
        private int GroupCount(PscLimitGroup g, PscHaulUnit unit)
        {
            if (g.countMode == PscGroupCountMode.Stacks) return GetGroupPhysicalStackCount(g, unit);
            int sum = 0;
            var ms = g.members;
            for (int i = 0; i < ms.Count; i++)
                if (ms[i] != null) sum += GetCount(ms[i], unit);
            return sum;
        }

        // Items mode adds reserved-inbound; Stacks (cell) mode is PHYSICAL-ONLY — counting future cells
        // is fuzzy, so concurrent overshoot is left to the over-cap drain to trim (mirrors per-tile).
        private int GroupEffectiveCount(PscLimitGroup g, PscHaulUnit unit)
        {
            if (g.countMode == PscGroupCountMode.Stacks) return GetGroupPhysicalStackCount(g, unit);
            int sum = 0;
            var ms = g.members;
            for (int i = 0; i < ms.Count; i++)
                if (ms[i] != null) sum += GetEffectiveCount(ms[i], unit);
            return sum;
        }

        // No-reentry mode-aware member sum, read straight from the cache (NOT GetCount): items from
        // `counts`, cells from `physicalStackCounts`. Used by the refill recompute paths, which have
        // already forced dirty siblings fresh via RecountDef.
        private int GroupModeSumFromCounts(PscLimitGroup g)
        {
            bool cells = g.countMode == PscGroupCountMode.Stacks;
            int sum = 0;
            var ms = g.members;
            for (int i = 0; i < ms.Count; i++)
            {
                var d = ms[i];
                if (d == null) continue;
                if (cells) { physicalStackCounts.TryGetValue(d, out var sc); sum += sc; }
                else { counts.TryGetValue(d, out var c); sum += c; }
            }
            return sum;
        }

        // No-reentry "any member toppable" check (reads the caches directly) for the cell-mode refill edge.
        private bool GroupHasMergeRoomFromCounts(PscLimitGroup g)
        {
            var ms = g.members;
            for (int i = 0; i < ms.Count; i++)
            {
                var d = ms[i];
                if (d == null) continue;
                physicalStackCounts.TryGetValue(d, out var sc);
                if (sc <= 0) continue;
                counts.TryGetValue(d, out var c);
                if (sc * Mathf.Max(1, d.stackLimit) > c) return true;
            }
            return false;
        }

        // A coarse upper-bound on ITEMS of `def` admissible before the group (or per-def limit) is full.
        //   ungrouped         -> upper - effective/physical items
        //   grouped, Items    -> upper - group item-sum
        //   grouped, Stacks   -> CELL room converted to items (CellsModeItemRoom)
        // Used by the batch destination-room gate. The precise per-cell enforcement for a Stacks (cell)
        // group lives in the cell-aware seams (PscGroupCells: the NoStorageBlockersIn steer + the drop /
        // haul-count cell clamp); this is only the coarse "could a batch ever fit" bound.
        public int GroupAwareItemRoom(ThingDef def, PscHaulUnit unit, int upper, bool includeReserved)
        {
            if (def != null && defToGroup.TryGetValue(def, out var g))
            {
                if (g.countMode == PscGroupCountMode.Stacks)
                    return CellsModeItemRoom(g, def, unit, upper);
                int used = includeReserved ? GroupEffectiveCount(g, unit) : GroupCount(g, unit);
                return Mathf.Max(0, upper - used);
            }
            int n = includeReserved ? GetEffectiveCount(def, unit) : GetCount(def, unit);
            return Mathf.Max(0, upper - n);
        }

        // Coarse item room for a CELL-cap (Stacks) group: free cells worth of `def` plus the slack in
        // `def`'s existing partial cells. Over cap -> 0 (drain only). At cap -> only the partial slack
        // (top off existing cells, no new cell). Below cap -> partial slack + freeCells*stackLimit.
        private int CellsModeItemRoom(PscLimitGroup g, ThingDef def, PscHaulUnit unit, int upperCells)
        {
            int s = Mathf.Max(1, def.stackLimit);
            int freeCells = upperCells - GetGroupPhysicalStackCount(g, unit);
            int mergeSlack = Mathf.Max(0, GetPhysicalStackCount(def, unit) * s - GetCount(def, unit));
            if (freeCells < 0) return 0;
            return freeCells == 0 ? mergeSlack : mergeSlack + freeCells * s;
        }

        // Over-cap drain budget for `def` in `unit` (physical). Ungrouped: count - per-def upper.
        // Grouped: the group is over its SHARED cap by (groupSum - upper), but to avoid an N-fold
        // double-drain (every member reading misplaced and removing the full excess), only ONE member
        // — the largest by count, tie-broken by shortHash — is ever the drain member, and its budget is
        // clamped to its own count. The admission misplaced-flag and the haul-count clamp both call
        // this, so they always pick the same member and can't drift. Returns false (excess 0) when not
        // draining. Each recompute shrinks the budget; the group converges to exactly the cap.
        public bool TryGetDrainExcess(ThingDef def, PscHaulUnit unit, out int excess)
        {
            excess = 0;
            if (def == null) return false;
            if (defToGroup.TryGetValue(def, out var g))
            {
                if (g.limit == null || !g.limit.Upper.HasValue) return false;
                int over = GroupCount(g, unit) - g.limit.Upper.Value;   // mode-aware (items or CELLS)
                if (over <= 0) return false;
                if (SelectDrainMember(g, unit) != def) return false;     // only the chosen member drains
                int memberItems = GetCount(def, unit);
                if (g.countMode == PscGroupCountMode.Stacks)
                {
                    // `over` is in CELLS; evict enough items to free `over` of the chosen member's cells.
                    // Without per-stack sizes, remove `over` average-cell amounts (ceil), clamped to the
                    // member's total. Slightly over-drains fragmented cells, but converges each recompute.
                    int memberCells = GetPhysicalStackCount(def, unit);
                    if (memberCells <= 0) return false;
                    int drainCells = Mathf.Min(over, memberCells);
                    int perCell = Mathf.Max(1, Mathf.CeilToInt(memberItems / (float)memberCells));
                    excess = Mathf.Min(memberItems, drainCells * perCell);
                }
                else
                {
                    excess = Mathf.Min(over, memberItems);
                }
                return excess > 0;
            }
            var lim = GetLimit(def);
            if (lim == null || !lim.Upper.HasValue) return false;
            int n = GetCount(def, unit);
            if (n <= lim.Upper.Value) return false;
            excess = n - lim.Upper.Value;
            return true;
        }

        // The single deterministic drain member, tie-broken by the lower shortHash so admission and the
        // clamp agree frame-to-frame. Mode-aware ranking: in Items mode, largest item count; in Stacks
        // (cell) mode, MOST CELLS first (then item count), so the member occupying the most cells is
        // drained — freeing whole cells fastest.
        private ThingDef SelectDrainMember(PscLimitGroup g, PscHaulUnit unit)
        {
            bool stacks = g.countMode == PscGroupCountMode.Stacks;
            ThingDef best = null;
            int bestKey = -1, bestItems = -1;
            var ms = g.members;
            for (int i = 0; i < ms.Count; i++)
            {
                var d = ms[i];
                if (d == null) continue;
                int items = GetCount(d, unit);
                if (items <= 0) continue;
                int key = stacks ? GetPhysicalStackCount(d, unit) : items;
                bool better = key > bestKey
                    || (key == bestKey && items > bestItems)
                    || (key == bestKey && items == bestItems && (best == null || d.shortHash < best.shortHash));
                if (better) { best = d; bestKey = key; bestItems = items; }
            }
            return best;
        }

        public int GetReservedInbound(ThingDef def)
            => reservedInbound.TryGetValue(def, out var r) ? r : 0;

        // Cheap "is any reservation outstanding" probe — lets the periodic rebuild skip the pawn scan for a
        // settled unit (drift can only exist where reserved > 0).
        public bool HasAnyReserved() => reservedInbound.Count > 0;

        // Add (or, with a negative amount, subtract) reserved-inbound for a def. The `<= 0 => Remove`
        // branch is the >= 0 floor: an unreserved/relocate decrement can never drive reserved negative
        // (which would make effective < physical and reopen the overshoot it prevents). Best-effort; the
        // periodic rebuild trues it up.
        public void AddReservedInbound(ThingDef def, int amount)
        {
            if (def == null || amount == 0) return;
            reservedInbound.TryGetValue(def, out var cur);
            int v = cur + amount;
            if (v <= 0) reservedInbound.Remove(def);
            else reservedInbound[def] = v;
        }

        public void ClearReservedInbound()
        {
            if (reservedInbound.Count > 0) reservedInbound.Clear();
        }

        private void RecomputeAll(PscHaulUnit unit)
        {
            counts.Clear();
            physicalStackCounts.Clear();
            if (unit.IsValid)
            {
                var held = unit.HeldThings;
                if (held != null)
                {
                    foreach (var t in held)
                    {
                        if (t == null) continue;
                        counts.TryGetValue(t.def, out var c);
                        counts[t.def] = c + t.stackCount;
                        physicalStackCounts.TryGetValue(t.def, out var sc);
                        physicalStackCounts[t.def] = sc + 1;   // diagnostics: one Thing = one physical stack
                    }
                }
            }
            allDirty = false;
            dirtyDefs.Clear();
            foreach (var kv in limits)
            {
                counts.TryGetValue(kv.Key, out var c);
                UpdateRefilling(kv.Value, c);
            }
            // Group pass: one refill state per group, derived from the mode-aware member sum. Member
            // counts are already in `counts` from the single HeldThings walk above, so this adds no scan.
            // GroupModeSumFromCounts reads `counts` directly (no GetCount), so no re-entry here.
            if (limitGroups != null)
            {
                for (int gi = 0; gi < limitGroups.Count; gi++)
                {
                    var g = limitGroups[gi];
                    if (g == null || g.limit == null) continue;
                    UpdateGroupRefillFromCounts(g);
                }
            }
        }

        private void RecomputeDef(ThingDef def, PscHaulUnit unit)
        {
            RecountDef(def, unit);
            // A grouped def carries no per-def limit; its hysteresis lives on the GROUP, recomputed
            // against the full member sum whenever ANY member is dirtied.
            var g = GroupOf(def);
            if (g != null) { UpdateGroupRefilling(g, unit); return; }
            counts.TryGetValue(def, out var c);
            UpdateRefilling(GetLimit(def), c);
        }

        // Recount one def into `counts` and clear its dirty flag — WITHOUT touching refill, so the
        // group-refill path can force dirty siblings fresh without re-entering RecomputeDef.
        private void RecountDef(ThingDef def, PscHaulUnit unit)
        {
            int sum = 0;
            int stacks = 0;
            if (unit.IsValid)
            {
                var held = unit.HeldThings;
                if (held != null)
                {
                    foreach (var t in held)
                    {
                        if (t != null && t.def == def) { sum += t.stackCount; stacks++; }
                    }
                }
            }
            counts[def] = sum;
            physicalStackCounts[def] = stacks;   // diagnostics: physical occupied stacks of this def
            dirtyDefs.Remove(def);
        }

        // Recompute a group's single refill state from its member sum, forcing any still-dirty sibling
        // fresh via the inner RecountDef (never the public GetCount, which would re-enter RecomputeDef).
        private void UpdateGroupRefilling(PscLimitGroup g, PscHaulUnit unit)
        {
            if (g.limit == null) return;
            // Force any still-dirty sibling fresh via the inner RecountDef, then sum from `counts`
            // (mode-aware) without re-entering GetCount/RecomputeDef.
            var ms = g.members;
            for (int i = 0; i < ms.Count; i++)
            {
                var d = ms[i];
                if (d != null && dirtyDefs.Contains(d)) RecountDef(d, unit);
            }
            UpdateGroupRefillFromCounts(g);
        }

        // Group refill edge from the caches (no reentry). Items mode: Satisfied at count >= upper. Cell
        // (Stacks) mode: Satisfied only when cells >= upper AND no member has a toppable partial — so a
        // lower/upper cell group keeps filling its cells (topping partials) before going Satisfied, rather
        // than stalling at "cap cells but not physically full".
        private void UpdateGroupRefillFromCounts(PscLimitGroup g)
        {
            var lim = g.limit;
            if (lim == null) return;
            int count = GroupModeSumFromCounts(g);
            bool full = lim.Upper.HasValue && count >= lim.Upper.Value
                && (g.countMode != PscGroupCountMode.Stacks || !GroupHasMergeRoomFromCounts(g));
            SetRefillEdge(lim, count, full);
        }

        // Hysteresis edge logic (D15), writing the PERSISTED lim.refill: ON (Refilling) at count <= lower,
        // OFF (Satisfied) when `full`, keep the known state in the open band. Fed PHYSICAL count only
        // (reserved-inbound is a planning overlay; hysteresis on effective would flip OFF on merely-reserved
        // hauls). lower unset => not refill-relevant => Unset. A still-Unset def in the band is treated as
        // "never evaluated" and defaults to filling.
        private static void SetRefillEdge(PscDefLimit lim, int count, bool full)
        {
            if (!lim.Lower.HasValue) { lim.refill = PscRefillState.Unset; return; }
            if (full) lim.refill = PscRefillState.Satisfied;
            else if (count <= lim.Lower.Value) lim.refill = PscRefillState.Refilling;
            else if (lim.refill == PscRefillState.Unset) lim.refill = PscRefillState.Refilling;
        }

        // Per-def / 2-arg refill edge (Satisfied at count >= upper). Used by the per-def passes.
        private void UpdateRefilling(PscDefLimit lim, int count)
        {
            if (lim == null) return;
            SetRefillEdge(lim, count, lim.Upper.HasValue && count >= lim.Upper.Value);
        }

        // Called when a limit is created/changed via the UI. Seeds refill state ON (so a freshly
        // limited stockpile fills up to upper before hysteresis takes over), unless already full.
        public void Notify_LimitSet(ThingDef def, PscHaulUnit unit)
        {
            MarkDirty(def);
            var lim = GetLimit(def);
            if (lim != null && lim.Lower.HasValue)
            {
                int c = GetCount(def, unit);
                lim.refill = (lim.Upper.HasValue && c >= lim.Upper.Value)
                    ? PscRefillState.Satisfied : PscRefillState.Refilling;
            }
        }

        // Seeds refill state ON for every per-def limit from current contents (multi-def UI apply or
        // migration). The whole-set analogue of Notify_LimitSet: a freshly limited def below its upper
        // fills up before hysteresis takes over.
        public void Notify_LimitsSeeded(PscHaulUnit unit)
        {
            MarkAllDirty();
            foreach (var kv in limits)
            {
                var lim = kv.Value;
                if (lim == null || lim.IsDefault || !lim.Lower.HasValue) continue;
                int c = GetCount(kv.Key, unit);
                lim.refill = (lim.Upper.HasValue && c >= lim.Upper.Value)
                    ? PscRefillState.Satisfied : PscRefillState.Refilling;
            }
        }

        // Seeds refill ON for every group from current contents (paste / migration). The whole-set
        // analogue of Notify_GroupLimitSet, paired with Notify_LimitsSeeded on the paste path.
        public void Notify_GroupsSeeded(PscHaulUnit unit)
        {
            if (limitGroups == null) return;
            for (int gi = 0; gi < limitGroups.Count; gi++)
                Notify_GroupLimitSet(limitGroups[gi], unit);
        }

        // Group analogue of Notify_LimitSet: seed a group's shared refill ON from the member sum when
        // its limit is created/changed via the UI (fresh policy fills up before hysteresis takes over).
        public void Notify_GroupLimitSet(PscLimitGroup g, PscHaulUnit unit)
        {
            MarkAllDirty();
            if (g?.limit == null || !g.limit.Lower.HasValue) return;
            // Edit-time seeder (not called from a recompute), so the public mode-aware GroupCount is safe.
            int sum = GroupCount(g, unit);
            bool full = g.limit.Upper.HasValue && sum >= g.limit.Upper.Value
                && (g.countMode != PscGroupCountMode.Stacks || !GroupAnyMergeRoom(g, unit));
            g.limit.refill = full ? PscRefillState.Satisfied : PscRefillState.Refilling;
        }

        // Tightens every per-def limit to what this unit can physically hold, matching the limit
        // editor's slider cap (slots * stackLimit). Called on paste so a policy copied from a larger
        // unit cannot leave an over-capacity number on a smaller one. slots is floored by the current
        // held-stack count (TryGetStackSlots), so this never clamps below already-stored contents and
        // so never trips the over-cap drain on valid contents. Per-def: each def caps at its own
        // stackLimit. No-op when the unit has no cells or does not resolve. Call BEFORE
        // Notify_LimitsSeeded so hysteresis is re-derived against the realistic (clamped) upper.
        public void ClampLimitsToCapacity(PscHaulUnit unit)
        {
            if (!unit.IsValid || !unit.TryGetStackSlots(out int slots)) return;
            foreach (var kv in limits)
            {
                var lim = kv.Value;
                if (kv.Key == null || lim == null || lim.IsDefault) continue;
                int max = slots * Mathf.Max(1, kv.Key.stackLimit);
                int? lo = lim.Lower, up = lim.Upper;
                PscDefLimit.ClampPair(ref lo, ref up, max);
                lim.Lower = lo;
                lim.Upper = up;
            }
            // Groups: a shared total spans defs, so cap against slots * the largest member stackLimit
            // (a generous unit-capacity basis — the hard cap still enforces real physical room).
            if (limitGroups != null)
            {
                for (int gi = 0; gi < limitGroups.Count; gi++)
                {
                    var g = limitGroups[gi];
                    if (g?.limit == null || g.limit.IsDefault) continue;
                    int max;
                    if (g.countMode == PscGroupCountMode.Stacks)
                    {
                        // Limit is in occupied cells; a group can occupy at most `slots` cells.
                        max = Mathf.Max(1, slots);
                    }
                    else
                    {
                        // Items basis: a shared total spans defs, so cap against slots * largest member
                        // stackLimit (generous — the hard cap still enforces real physical room).
                        int maxStack = 1;
                        for (int i = 0; i < g.members.Count; i++)
                            if (g.members[i] != null) maxStack = Mathf.Max(maxStack, g.members[i].stackLimit);
                        max = slots * maxStack;
                    }
                    int? lo = g.limit.Lower, up = g.limit.Upper;
                    PscDefLimit.ClampPair(ref lo, ref up, max);
                    g.limit.Lower = lo;
                    g.limit.Upper = up;
                }
            }
            MarkAllDirty();
        }

        // Deep copy of policy only (never counts — those are unit-specific and recomputed).
        public void CopyPolicyFrom(PscStorageData other)
        {
            limits = new Dictionary<ThingDef, PscDefLimit>();
            foreach (var kv in other.limits)
            {
                if (kv.Key != null && kv.Value != null && !kv.Value.IsDefault)
                    limits[kv.Key] = kv.Value.Clone();
            }
            batch = other.batch;
            batchEmpty = other.batchEmpty;
            onlyFromSource = other.onlyFromSource;
            onlyToDestinations = other.onlyToDestinations;
            subTier = other.subTier;
            letter = other.letter;
            mode = other.mode;
            perTileLimit = other.perTileLimit;
            alarm = (other.alarm != null && other.alarm.IsActive) ? other.alarm.Clone() : null;
            CopyGroupsFrom(other);
            // refill state rides each cloned PscDefLimit and is reset to Unset by Clone (fresh policy, not a
            // restore); the caller re-seeds it via Notify_LimitsSeeded / Notify_GroupLimitSet.
            reservedInbound.Clear();
            MarkAllDirty();
        }

        // Deep-copy limit groups from `other` and rebuild the reverse index (which also strips any
        // grouped def from the freshly-copied `limits`). Shared by CopyPolicyFrom and CopyScopedFrom.
        // Copies every NON-EMPTY group (>= 1 member), including an unconfigured/named draft — drafts are
        // persistent policy (HasPersistentPolicy), so copy/paste must preserve them, not silently drop them.
        private void CopyGroupsFrom(PscStorageData other)
        {
            limitGroups = new List<PscLimitGroup>();
            if (other?.limitGroups != null)
            {
                for (int i = 0; i < other.limitGroups.Count; i++)
                {
                    var g = other.limitGroups[i];
                    if (g != null && g.members != null && g.members.Count > 0) limitGroups.Add(g.Clone());
                }
            }
            RebuildGroupIndex();
        }

        // Scoped copy for the right-click "paste only some settings" menu. Replaces the IN-SCOPE fields
        // on this from `other` (a null `other` clears them), leaving out-of-scope policy untouched.
        // Never copies fine-order subTier/letter or the vanilla Priority band. The vanilla allow/disallow
        // filter and the feeder routes themselves live off PscStorageData and are applied by the caller
        // (PscScopedPaste.Apply).
        public void CopyScopedFrom(PscStorageData other, PscPasteScope scope)
        {
            // Items & limits — every scope (groups are a limits facet, so they copy here too).
            limits = new Dictionary<ThingDef, PscDefLimit>();
            if (other != null)
            {
                foreach (var kv in other.limits)
                {
                    if (kv.Key != null && kv.Value != null && !kv.Value.IsDefault)
                        limits[kv.Key] = kv.Value.Clone();
                }
            }
            CopyGroupsFrom(other);

            // Routes scope and wider — the pull-only / push-only flags.
            if (scope >= PscPasteScope.ItemsLimitsRoutes)
            {
                onlyFromSource = other?.onlyFromSource ?? false;
                onlyToDestinations = other?.onlyToDestinations ?? false;
            }

            // Everything-but-priorities — batch, storage mode, and alarm.
            if (scope >= PscPasteScope.EverythingButPriorities)
            {
                batch = other?.batch ?? 0;
                batchEmpty = other?.batchEmpty ?? 0;
                mode = other?.mode ?? PscStorageMode.Normal;
                perTileLimit = other?.perTileLimit ?? 0;
                alarm = (other?.alarm != null && other.alarm.IsActive) ? other.alarm.Clone() : null;
            }

            // refill state rides each cloned PscDefLimit (Clone resets it); re-seeded by the caller.
            reservedInbound.Clear();
            MarkAllDirty();
        }

        public void ExposeData()
        {
            // logNullErrors:false — when a CONTENT mod is removed but PSC is kept, per-def keys for
            // its defs no longer resolve. The PostLoadInit prune below drops those entries; passing
            // false keeps vanilla's BuildDictionary from logging a red error per missing key first,
            // so the documented "silent self-heal on load" is actually silent. (Working lists are
            // throwaway scratch — the full Look overload is the only one exposing logNullErrors.)
            List<ThingDef> limitsKeysWork = null;
            List<PscDefLimit> limitsValuesWork = null;
            Scribe_Collections.Look(ref limits, "limits", LookMode.Def, LookMode.Deep,
                ref limitsKeysWork, ref limitsValuesWork, logNullErrors: false);
            Scribe_Values.Look(ref batch, "batch", 0);
            Scribe_Values.Look(ref batchEmpty, "batchEmpty", 0);
            Scribe_Values.Look(ref onlyFromSource, "onlyFromSource", false);
            Scribe_Values.Look(ref onlyToDestinations, "onlyToDestinations", false);
            Scribe_Values.Look(ref subTier, "subTier", (byte)0);
            Scribe_Values.Look(ref letter, "letter");
            Scribe_Values.Look(ref mode, "mode", PscStorageMode.Normal);
            Scribe_Values.Look(ref perTileLimit, "perTileLimit", 0);

            // Alarm: write the <alarm> child only when armed (write-nothing-when-empty contract). On
            // load (LoadingVars) read it back; null stays the valid "no alarm" state (no PostLoadInit
            // guard needed).
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                if (alarm != null && alarm.IsActive)
                    Scribe_Deep.Look(ref alarm, "alarm");
            }
            else
            {
                Scribe_Deep.Look(ref alarm, "alarm");
            }

            // Limit groups: write the <groups> node only when non-empty (write-nothing-when-empty), so a
            // groupless unit and a removed-mod save stay clean. Members persist as defNames inside each
            // PscLimitGroup (LookMode.Value), so a missing content def loads silently.
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                if (limitGroups != null && limitGroups.Count > 0)
                    Scribe_Collections.Look(ref limitGroups, "groups", LookMode.Deep);
            }
            else
            {
                Scribe_Collections.Look(ref limitGroups, "groups", LookMode.Deep);
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (limits == null) limits = new Dictionary<ThingDef, PscDefLimit>();
                // Drop entries whose def no longer resolves (mod removed) or that are default.
                List<ThingDef> toRemove = null;
                foreach (var kv in limits)
                {
                    if (kv.Key == null || kv.Value == null || kv.Value.IsDefault)
                        (toRemove ??= new List<ThingDef>()).Add(kv.Key);
                }
                if (toRemove != null)
                    foreach (var k in toRemove) limits.Remove(k);
                if (perTileLimit < 0) perTileLimit = 0;   // defensive: a stray negative degrades to off
                // Groups self-heal: resolve member names, drop unresolved, prune only fully-empty groups
                // (a 1-member group stays a valid draft), dedupe letters, strip grouped defs from
                // `limits`, and rebuild the reverse index. Runs after the limits prune so a def claimed by
                // both heals group-wins-and-strip-limits.
                if (limitGroups == null) limitGroups = new List<PscLimitGroup>();
                NormalizeGroups();
                allDirty = true;
                reservedInbound.Clear();   // runtime-only; rebuilt from active jobs after load
            }
        }
    }
}
