using System.Collections.Generic;
using Verse;

namespace PrecisionStockpileControl
{
    // Per-def limit policy. Limits are tracked in ITEMS, never stacks (stacks are a display-only
    // convenience). Stored as int sentinels (-1 = unset) rather than nullable ints to keep
    // Scribe_Values round-tripping boringly safe across versions.
    public class PscDefLimit : IExposable
    {
        public int lowerRaw = -1;   // -1 = unset (D15: "always refill"); 0 = only when empty; N = refill at <= N
        public int upperRaw = -1;   // -1 = unset (unlimited); N = the maximum (hard cap at drop time)

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

        public PscDefLimit Clone() => new PscDefLimit { lowerRaw = lowerRaw, upperRaw = upperRaw };

        public void ExposeData()
        {
            Scribe_Values.Look(ref lowerRaw, "lower", -1);
            Scribe_Values.Look(ref upperRaw, "upper", -1);
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

        // Stockpile alarm (opt-in): null when unused, so a unit with no alarm writes nothing and
        // costs nothing. Holds the high/low fullness thresholds, dwell, cadence, notify style, and
        // its own persisted edge state. Shared across a linked StorageGroup like the rest of policy.
        public PscAlarmConfig alarm;

        // ---- Runtime cache (never scribed; rebuilt from HeldThings) ----
        private readonly Dictionary<ThingDef, int> counts = new Dictionary<ThingDef, int>();
        private readonly HashSet<ThingDef> refilling = new HashSet<ThingDef>();   // hysteresis: present = currently refilling
        private readonly HashSet<ThingDef> dirtyDefs = new HashSet<ThingDef>();
        private bool allDirty = true;

        public bool HasPersistentPolicy =>
            limits.Count > 0
            || batch > 0 || batchEmpty > 0 || onlyFromSource || onlyToDestinations
            || subTier != 0 || !string.IsNullOrEmpty(letter)
            || mode != PscStorageMode.Normal
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

        // ---- Dirty marking (cheap; called from drift-seam patches) ----
        public void MarkDirty(ThingDef def) { if (def != null) dirtyDefs.Add(def); }
        public void MarkAllDirty() { allDirty = true; }

        // ---- Count access (recomputes from the unit's HeldThings when dirty) ----
        public int GetCount(ThingDef def, PscHaulUnit unit)
        {
            if (allDirty) RecomputeAll(unit);
            else if (dirtyDefs.Contains(def)) RecomputeDef(def, unit);
            return counts.TryGetValue(def, out var c) ? c : 0;
        }

        public bool IsRefilling(ThingDef def) => refilling.Contains(def);

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

        // Hysteresis edge logic (D15): refill turns ON at count <= lower, OFF at count >= upper.
        // lower unset => "always refill" => refilling set unused (admission ignores it).
        private void UpdateRefilling(ThingDef def, int count, PscDefLimit lim)
        {
            if (lim == null || !lim.Lower.HasValue) { refilling.Remove(def); return; }
            if (lim.Upper.HasValue && count >= lim.Upper.Value) refilling.Remove(def);
            else if (count <= lim.Lower.Value) refilling.Add(def);
            // else: between thresholds — keep current state (hysteresis)
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
                if (!(lim.Upper.HasValue && c >= lim.Upper.Value)) refilling.Add(def);
                else refilling.Remove(def);
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
                if (!(lim.Upper.HasValue && c >= lim.Upper.Value)) refilling.Add(kv.Key);
                else refilling.Remove(kv.Key);
            }
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
            alarm = (other.alarm != null && other.alarm.IsActive) ? other.alarm.Clone() : null;
            refilling.Clear();
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
                alarm = (other?.alarm != null && other.alarm.IsActive) ? other.alarm.Clone() : null;
            }

            refilling.Clear();
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
                allDirty = true;
            }
        }
    }
}
