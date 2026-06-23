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

        // ---- Runtime cache (never scribed; rebuilt from HeldThings) ----
        // Hysteresis state is NOT here anymore — it is persisted on each PscDefLimit (lim.refill).
        private readonly Dictionary<ThingDef, int> counts = new Dictionary<ThingDef, int>();
        private readonly HashSet<ThingDef> dirtyDefs = new HashSet<ThingDef>();
        private bool allDirty = true;

        // Reservation-aware fill counting (opt-in, on by default). Per-def items committed to haul IN
        // but not yet delivered. NEVER scribed; this is the "fast delta" half of the split counter —
        // physical `counts` stays the source of truth, and PscMapComponent's periodic rebuild-from-
        // active-jobs corrects any drift. Only the soft planning gates read counts+reservedInbound
        // (GetEffectiveCount); the hard drop cap, over-cap drain, and freeze reads stay on physical.
        private readonly Dictionary<ThingDef, int> reservedInbound = new Dictionary<ThingDef, int>();

        public bool HasPersistentPolicy =>
            limits.Count > 0
            || batch > 0 || batchEmpty > 0 || onlyFromSource || onlyToDestinations
            || subTier != 0 || !string.IsNullOrEmpty(letter)
            || mode != PscStorageMode.Normal
            || perTileLimit > 0
            || (alarm != null && alarm.IsActive);

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
                return false;
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
                return false;
            }
        }

        // ---- Dirty marking (cheap; called from drift-seam patches) ----
        public void MarkDirty(ThingDef def) { if (def != null) dirtyDefs.Add(def); }
        public void MarkAllDirty() { allDirty = true; rankCacheGen = -1; }

        // Cached fine-order rank (PscOrder.ComputeRankWithinBand). Runtime-only, never scribed. Recompute
        // only when the band changes — the band self-compare also transparently catches a vanilla
        // priority-button edit that has no PSC seam — or the global numbering generation bumps. Any policy
        // edit (subTier/letter included) invalidates via MarkAllDirty resetting rankCacheGen above. Plain
        // ints keep the read branch tiny; -1 generation forces a recompute on first read / after a fresh
        // CopyPolicyFrom.
        // Thread-safety: GetRank can run on off-main reachability threads (the feeder Outranks gate). The
        // unsynchronised write is a benign, self-correcting race — every thread computes the IDENTICAL
        // value for the same (subTier, letter, band, gen), and settings.Priority is stable across a scan,
        // so the three fields never combine into a wrong-band rank. Matches the count/reserved best-effort
        // cache posture; ints/byte writes are atomic, so no structural corruption is possible.
        private int rankCache;
        private int rankCacheGen = -1;                                    // -1 forces a recompute on first read
        private StoragePriority rankCacheBand = (StoragePriority)byte.MaxValue;   // impossible-band sentinel

        public int GetRank(StoragePriority band)
        {
            if (rankCacheGen == PscOrder.NumberingGeneration && rankCacheBand == band) return rankCache;
            rankCache = PscOrder.ComputeRankWithinBand(subTier, letter, band);
            rankCacheGen = PscOrder.NumberingGeneration;
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
                    }
                }
            }
            allDirty = false;
            dirtyDefs.Clear();
            foreach (var kv in limits)
            {
                counts.TryGetValue(kv.Key, out var c);
                UpdateRefilling(kv.Key, c, kv.Value);
            }
        }

        private void RecomputeDef(ThingDef def, PscHaulUnit unit)
        {
            int sum = 0;
            if (unit.IsValid)
            {
                var held = unit.HeldThings;
                if (held != null)
                {
                    foreach (var t in held)
                    {
                        if (t != null && t.def == def) sum += t.stackCount;
                    }
                }
            }
            counts[def] = sum;
            dirtyDefs.Remove(def);
            UpdateRefilling(def, sum, GetLimit(def));
        }

        // Hysteresis edge logic (D15), writing the PERSISTED lim.refill: ON (Refilling) at count <= lower,
        // OFF (Satisfied) at count >= upper. Fed PHYSICAL count only (reserved-inbound is a planning overlay;
        // hysteresis on effective would flip OFF on merely-reserved hauls). lower unset => not refill-relevant
        // => Unset (admission ignores it). In the open band a KNOWN state is kept (the whole point of
        // hysteresis, now persistent); a still-Unset def there is treated as "never evaluated" and defaults
        // to filling, so a brand-new/pre-field limit fills to upper before the OFF edge can ever fire.
        private void UpdateRefilling(ThingDef def, int count, PscDefLimit lim)
        {
            if (lim == null || !lim.Lower.HasValue) { if (lim != null) lim.refill = PscRefillState.Unset; return; }
            if (lim.Upper.HasValue && count >= lim.Upper.Value) lim.refill = PscRefillState.Satisfied;
            else if (count <= lim.Lower.Value) lim.refill = PscRefillState.Refilling;
            else if (lim.refill == PscRefillState.Unset) lim.refill = PscRefillState.Refilling; // never-evaluated -> default to filling
            // else: between thresholds with a known state — keep (persisted hysteresis)
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
            // refill state rides each cloned PscDefLimit and is reset to Unset by Clone (fresh policy, not a
            // restore); the caller re-seeds it via Notify_LimitsSeeded.
            reservedInbound.Clear();
            MarkAllDirty();
        }

        // Scoped copy for the right-click "paste only some settings" menu. Replaces the IN-SCOPE fields
        // on this from `other` (a null `other` clears them), leaving out-of-scope policy untouched.
        // Never copies fine-order subTier/letter or the vanilla Priority band. The vanilla allow/disallow
        // filter and the feeder routes themselves live off PscStorageData and are applied by the caller
        // (PscScopedPaste.Apply).
        public void CopyScopedFrom(PscStorageData other, PscPasteScope scope)
        {
            // Items & limits — every scope.
            limits = new Dictionary<ThingDef, PscDefLimit>();
            if (other != null)
            {
                foreach (var kv in other.limits)
                {
                    if (kv.Key != null && kv.Value != null && !kv.Value.IsDefault)
                        limits[kv.Key] = kv.Value.Clone();
                }
            }

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
            Scribe_Values.Look(ref perTileLimit, "perTile", 0);

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
                allDirty = true;
                reservedInbound.Clear();   // runtime-only; rebuilt from active jobs after load
            }
        }
    }
}
