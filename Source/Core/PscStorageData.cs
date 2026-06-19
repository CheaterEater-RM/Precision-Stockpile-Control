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
        public int upperRaw = -1;   // -1 = unset (unlimited); N = target maximum (soft cap until M2)

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
        public int batch;                 // batch fill (M2): 0 = off, never bring fewer than N in one trip
        public int batchEmpty;            // batch empty: 0 = off, never remove fewer than N out in one trip
        public bool onlyFromSource;       // groundwork (M3)
        public bool onlyToDestinations;   // groundwork (M3)
        public byte subTier;              // groundwork (M4): 0 = unset
        public string letter;             // groundwork (M4): null/empty = none

        // ---- Runtime cache (never scribed; rebuilt from HeldThings) ----
        private readonly Dictionary<ThingDef, int> counts = new Dictionary<ThingDef, int>();
        private readonly HashSet<ThingDef> refilling = new HashSet<ThingDef>();   // hysteresis: present = currently refilling
        private readonly HashSet<ThingDef> dirtyDefs = new HashSet<ThingDef>();
        private bool allDirty = true;

        public bool HasPersistentPolicy =>
            limits.Count > 0 || batch > 0 || batchEmpty > 0 || onlyFromSource || onlyToDestinations
            || subTier != 0 || !string.IsNullOrEmpty(letter);

        public bool HasLimit(ThingDef def)
        {
            return def != null && limits.TryGetValue(def, out var l) && l != null && !l.IsDefault;
        }

        public PscDefLimit GetLimit(ThingDef def)
        {
            return (def != null && limits.TryGetValue(def, out var l)) ? l : null;
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
            refilling.Clear();
            MarkAllDirty();
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref limits, "limits", LookMode.Def, LookMode.Deep);
            Scribe_Values.Look(ref batch, "batch", 0);
            Scribe_Values.Look(ref batchEmpty, "batchEmpty", 0);
            Scribe_Values.Look(ref onlyFromSource, "onlyFromSource", false);
            Scribe_Values.Look(ref onlyToDestinations, "onlyToDestinations", false);
            Scribe_Values.Look(ref subTier, "subTier", (byte)0);
            Scribe_Values.Look(ref letter, "letter");

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
