using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // ── One-way migration: import limits from other stockpile-limit mods ────────────────────────
    // When a player switches FROM another limit mod TO PSC (removes the old mod, adds PSC), the old
    // mod's data is still sitting in the save as child nodes INSIDE each vanilla <settings> node —
    // those mods (like PSC) persist via a postfix on StorageSettings.ExposeData. When the old mod is
    // gone, nobody consumes those nodes, so PSC's own ExposeData postfix reads them straight out of
    // Scribe.loader.curXmlParent during LoadingVars, with pure vanilla Scribe APIs (no reflection on
    // the foreign assembly, no save scan, no loadID matching).
    //
    // Two phases:
    //   1. Capture (LoadingVars, from the persistence postfix): read the orphaned raw values into a
    //      pending record keyed by the live StorageSettings instance. No conversion yet.
    //   2. Resolve (Game.FinalizeInit, from PscGameComponent): the storage unit (cells, capacity,
    //      allowed defs) is now resolvable — convert each pending record to PSC policy, register it,
    //      and tell the player what was imported.
    //
    // Idempotency: after the migrating load, the next save rebuilds <settings> from scratch and PSC
    // writes its own <psc> node; the orphaned foreign nodes vanish permanently. The presence of a
    // <psc> node is the "already migrated" marker (the persistence postfix never imports when one is
    // present), so no explicit flag is needed.
    //
    // Gating: a format is eligible only when its source mod is ABSENT (its marker type doesn't
    // resolve). If the old mod is still loaded, importing would double-enforce, so we skip it (and
    // dev-log it, so it's obvious why nothing migrated). Supported, by usage: Stack Gap, Satisfied
    // Storage, Variety Matters Stockpile. (Stockpile Limit support was dropped; Storage Sorting is
    // unsupported — zone-keyed MapComponent, not a <settings> child.)
    //
    // Failure posture: per Adrian — losing a limit is fine, corrupting storage is not. Every per-unit
    // conversion is wrapped; on any error we drop that unit's PSC data (back to plain allowed) and
    // count it as failed, then warn the player. A malformed import never leaves a stockpile stuck.
    internal static class PscMigration
    {
        internal enum SourceKind { StackGap, SatisfiedStorage, VarietyMatters }

        // Iterated in priority order for the summary letter / log (by real-world usage).
        private static readonly SourceKind[] Priority =
            { SourceKind.StackGap, SourceKind.SatisfiedStorage, SourceKind.VarietyMatters };

        // Marker types — when present, the source mod is loaded and we must NOT import (double-enforce).
        private const string TypeSatisfiedStorage = "SatisfiedStorage.Hysteresis";
        private const string TypeVarietyMatters = "VarietyMattersStockpile.StorageLimits";
        private const string TypeStackGap = "StorageUpperBound.StackGapData";

        // "Source mod absent" gates, resolved once (assemblies are all loaded before any load runs).
        private static readonly bool satisfiedStorageEnabled = AccessTools.TypeByName(TypeSatisfiedStorage) == null;
        private static readonly bool varietyMattersEnabled = AccessTools.TypeByName(TypeVarietyMatters) == null;
        private static readonly bool stackGapEnabled = AccessTools.TypeByName(TypeStackGap) == null;

        private const int SaneItemCap = 1000000;
        // Stack Gap's percent→items curve uses DisplayLogFactor (a global mod setting, default 3,
        // gone once the mod is removed). We assume the default; the result is approximate by nature.
        private const float StackGapLogFactor = 3f;

        // Raw captured values; converted later. One per StorageSettings that carried a foreign node.
        private sealed class Pending
        {
            public SourceKind kind;
            public float fillPercent = 100f;          // Satisfied Storage: 0-100 whole-zone refill
            public int dupLimit = -1;                 // Variety Matters: max duplicate stacks/def (-1 unset)
            public float cellFill = 1f;               // Variety Matters: 0-1 whole-zone refill
            public Dictionary<string, int> perItem;   // Stack Gap: defName -> absolute item cap
            public float gapMin;                      // Stack Gap: percent refill threshold (0 = none)
            public float gapMax = 1f;                 // Stack Gap: percent fill cap (1 = none)
        }

        private static readonly Dictionary<StorageSettings, Pending> pending
            = new Dictionary<StorageSettings, Pending>();

        // Formats whose data was found in the save but whose mod is still loaded (so we skipped the
        // import). Dev-logged once at resolve so it's obvious why nothing migrated.
        private static readonly HashSet<SourceKind> foundButLoaded = new HashSet<SourceKind>();

        private static bool AnyFormatEnabled => stackGapEnabled || satisfiedStorageEnabled || varietyMattersEnabled;

        // Cleared by PscGameComponent on every load (and after a resolve pass) so nothing leaks across
        // sessions.
        public static void ClearPending()
        {
            pending.Clear();
            foundButLoaded.Clear();
        }

        // ── Phase 1: capture (called from the persistence postfix, LoadingVars only) ──────────────
        public static void TryCaptureForeign(StorageSettings settings)
        {
            if (settings == null) return;
            // Nothing to import and not diagnosing -> skip the probe entirely.
            if (!AnyFormatEnabled && !PscLog.Enabled) return;
            try
            {
                var parent = Scribe.loader?.curXmlParent;
                if (parent == null) return;

                // Probe in priority order; the first foreign node found wins. When the node is present
                // but its mod is still loaded, record it for the dev-log notice instead of importing.
                var stackGap = parent["stackGap"];
                if (stackGap != null)
                {
                    if (stackGapEnabled) CaptureStackGap(settings, stackGap);
                    else NoteFoundButLoaded(SourceKind.StackGap);
                    return;
                }
                var hysteresis = parent["hysteresis"];
                if (hysteresis != null)
                {
                    if (satisfiedStorageEnabled) CaptureSatisfied(settings, hysteresis);
                    else NoteFoundButLoaded(SourceKind.SatisfiedStorage);
                    return;
                }
                var limitSettings = parent["limitSettings"];
                if (limitSettings != null)
                {
                    if (varietyMattersEnabled) CaptureVariety(settings, limitSettings);
                    else NoteFoundButLoaded(SourceKind.VarietyMatters);
                }
            }
            catch (Exception ex)
            {
                // Never let a malformed foreign node break the load.
                Log.Warning("[PSC] migration: failed to read a foreign limit node; skipping it. " + ex);
            }
        }

        private static void NoteFoundButLoaded(SourceKind kind)
        {
            if (foundButLoaded.Add(kind) && PscLog.Enabled)
                PscLog.Msg($"migrate: found {RawName(kind)} data in the save, but {RawName(kind)} is "
                    + "still loaded — skipping import (remove it to migrate).");
        }

        private static void CaptureSatisfied(StorageSettings settings, XmlNode node)
        {
            pending[settings] = new Pending
            {
                kind = SourceKind.SatisfiedStorage,
                fillPercent = FloatNode(node, "fillPercent", 100f),
            };
            if (PscLog.Enabled) PscLog.Msg($"migrate: captured Satisfied Storage (fillPercent={pending[settings].fillPercent})");
        }

        private static void CaptureVariety(StorageSettings settings, XmlNode node)
        {
            pending[settings] = new Pending
            {
                kind = SourceKind.VarietyMatters,
                dupLimit = IntNode(node, "duplicatesLimit", -1),
                cellFill = FloatNode(node, "cellfillPercentage", 1f),
            };
            if (PscLog.Enabled) PscLog.Msg($"migrate: captured Variety Matters (dupLimit={pending[settings].dupLimit}, cellFill={pending[settings].cellFill})");
        }

        // Stack Gap stores allowedPerItem as a Scribe_Collections<string,int> with LookMode.Value for
        // both key and value, which serializes as PARALLEL <keys>/<values> li-lists (NOT li/key/value).
        // It also stores stackGapPercents as a FloatRange ("min~max"): max = fill cap, min = refill
        // threshold. Both are optional; capture whatever is present.
        private static void CaptureStackGap(StorageSettings settings, XmlNode node)
        {
            var rec = new Pending { kind = SourceKind.StackGap };
            bool any = false;

            var dict = node["allowedPerItem"];
            var keys = ChildElements(dict?["keys"]);
            var vals = ChildElements(dict?["values"]);
            int n = Math.Min(keys.Count, vals.Count);
            for (int i = 0; i < n; i++)
            {
                string key = keys[i].InnerText;
                if (string.IsNullOrEmpty(key)) continue;
                if (!int.TryParse(vals[i].InnerText, out int v) || v < 0) continue;
                (rec.perItem ??= new Dictionary<string, int>())[key] = v;
                any = true;
            }

            if (TryParseRange(node, "stackGapPercents", out float min, out float max))
            {
                rec.gapMin = min;
                rec.gapMax = max;
                if (min > 0f || max < 1f) any = true;
            }

            if (!any) return;   // empty stackGap (no per-item caps, full 0~1 range) -> nothing to do
            pending[settings] = rec;
            if (PscLog.Enabled)
                PscLog.Msg($"migrate: captured Stack Gap (perItem={rec.perItem?.Count ?? 0}, range={rec.gapMin}~{rec.gapMax})");
        }

        // ── Phase 2: resolve + register (called from PscGameComponent.FinalizeInit) ───────────────
        public static void ResolveAllPending()
        {
            if (PscLog.Enabled)
                PscLog.Msg($"migrate: resolve start — gates[stackGap={GateWord(stackGapEnabled)}, "
                    + $"satisfied={GateWord(satisfiedStorageEnabled)}, vms={GateWord(varietyMattersEnabled)}], "
                    + $"pending={pending.Count}, foundButLoaded={foundButLoaded.Count}");

            if (pending.Count == 0) { foundButLoaded.Clear(); return; }
            try
            {
                var imported = new Dictionary<SourceKind, int>();
                int failed = 0;

                foreach (var kv in pending)
                {
                    var settings = kv.Key;
                    var rec = kv.Value;
                    try
                    {
                        var unit = PscHaulUnit.ResolveSettings(settings);
                        if (!unit.IsValid)
                        {
                            if (PscLog.Enabled) PscLog.Msg($"migrate: skipped {RawName(rec.kind)} — storage no longer resolvable");
                            continue;
                        }

                        var existing = PscStorageDataStore.TryGet(settings);
                        if (existing != null && existing.HasPersistentPolicy)
                        {
                            if (PscLog.Enabled) PscLog.Msg($"migrate: skipped {RawName(rec.kind)} on {unit.UniqueLoadID} — PSC policy already present");
                            continue;
                        }

                        var data = new PscStorageData();
                        if (!Convert(rec, unit, settings, data))
                        {
                            if (PscLog.Enabled) PscLog.Msg($"migrate: {RawName(rec.kind)} on {unit.UniqueLoadID} produced no policy (all defaults)");
                            continue;
                        }

                        PscStorageDataStore.Set(settings, data);
                        data.Notify_LimitsSeeded(unit);                      // seed refill from contents
                        PscMapComponent.NotifyPolicyChanged(settings);
                        imported.TryGetValue(rec.kind, out int c);
                        imported[rec.kind] = c + 1;
                        if (PscLog.Enabled)
                            PscLog.Msg($"migrate: imported {RawName(rec.kind)} onto {unit.UniqueLoadID} "
                                + $"(perDef={data.limits.Count})");
                    }
                    catch (Exception ex)
                    {
                        // Graceful fail: drop any partial data for this unit, leave it plain-allowed.
                        failed++;
                        PscStorageDataStore.Remove(settings);
                        Log.Warning($"[PSC] migration: failed to import limits for one stockpile; left unlimited. {ex}");
                    }
                }

                Announce(imported, failed);
            }
            catch (Exception ex)
            {
                Log.Warning("[PSC] migration: resolve pass failed. " + ex);
            }
            finally
            {
                pending.Clear();
                foundButLoaded.Clear();
            }
        }

        // Returns true when at least one limit was written into `data`.
        private static bool Convert(Pending rec, PscHaulUnit unit, StorageSettings settings, PscStorageData data)
        {
            switch (rec.kind)
            {
                case SourceKind.StackGap: return ConvertStackGap(rec, unit, settings, data);
                case SourceKind.SatisfiedStorage: return ConvertSatisfied(rec, unit, settings, data);
                case SourceKind.VarietyMatters: return ConvertVariety(rec, unit, settings, data);
                default: return false;
            }
        }

        // Stack Gap: allowedPerItem -> exact per-def upper caps; the stackGapPercents range -> a
        // per-def cap/refill applied across all currently-allowed defs, approximating Stack Gap's
        // per-stack pow curve (round(maxStack * percent^factor) per stack, x stack slots). Approximate
        // — the basis (global max stack size + display factor) lives in Stack Gap's settings, gone
        // once it's removed.
        private static bool ConvertStackGap(Pending rec, PscHaulUnit unit, StorageSettings settings, PscStorageData data)
        {
            bool any = false;

            if (rec.perItem != null)
            {
                foreach (var kv in rec.perItem)
                {
                    var def = DefDatabase<ThingDef>.GetNamedSilentFail(kv.Key);
                    if (def == null) continue;                   // def from a removed mod — skip
                    data.limits[def] = new PscDefLimit { Upper = Mathf.Clamp(kv.Value, 1, SaneItemCap) };
                    any = true;
                }
            }

            if (rec.gapMax < 1f || rec.gapMin > 0f)
            {
                int basis = MaxStackBasis(settings);
                Capacity(unit, out _, out int slots);
                slots = Mathf.Max(1, slots);
                int? defUpper = null, defLower = null;
                if (rec.gapMax < 1f)
                {
                    int perStack = Mathf.Max(1, Mathf.RoundToInt(basis * Mathf.Pow(rec.gapMax, StackGapLogFactor)));
                    defUpper = Mathf.Clamp(perStack * slots, 1, SaneItemCap);
                    any = true;
                }
                if (rec.gapMin > 0f)
                {
                    int perStack = Mathf.RoundToInt(basis * Mathf.Pow(rec.gapMin, StackGapLogFactor));
                    int lower = Mathf.Clamp(perStack * slots, 0, SaneItemCap);
                    if (defUpper.HasValue && lower > defUpper.Value) lower = defUpper.Value;
                    if (lower > 0) { defLower = lower; any = true; }
                }
                ApplyDefaultToAllowed(settings, data, defLower, defUpper);
            }
            return any;
        }

        // Satisfied Storage: whole-zone refill % -> a per-def refill threshold across all allowed
        // defs (no upper exists). Clean conceptual fit; the absolute item value uses an approximate
        // capacity basis.
        private static bool ConvertSatisfied(Pending rec, PscHaulUnit unit, StorageSettings settings, PscStorageData data)
        {
            if (rec.fillPercent >= 100f) return false;           // 100% = vanilla "refill when empty"
            int capacity = CapacityItems(unit, settings);
            int lower = Mathf.Clamp(Mathf.FloorToInt(capacity * (rec.fillPercent / 100f)), 0, SaneItemCap);
            if (lower <= 0) return false;
            ApplyDefaultToAllowed(settings, data, lower, null);
            return true;
        }

        // Variety Matters: duplicate-stack cap -> per-def upper (approx via max stack size); cell-fill
        // % -> per-def lower; both applied across all allowed defs. Per-stack-size cap is dropped
        // (PSC never touches def.stackLimit).
        private static bool ConvertVariety(Pending rec, PscHaulUnit unit, StorageSettings settings, PscStorageData data)
        {
            int? defUpper = null, defLower = null;
            if (rec.dupLimit >= 0)
                defUpper = Mathf.Clamp(rec.dupLimit * MaxStackBasis(settings), 1, SaneItemCap);
            if (rec.cellFill < 1f)
            {
                int capacity = CapacityItems(unit, settings);
                int lower = Mathf.Clamp(Mathf.FloorToInt(capacity * rec.cellFill), 0, SaneItemCap);
                if (lower > 0)
                {
                    if (defUpper.HasValue && lower > defUpper.Value) lower = defUpper.Value;
                    defLower = lower;
                }
            }
            if (defLower == null && defUpper == null) return false;
            ApplyDefaultToAllowed(settings, data, defLower, defUpper);
            return true;
        }

        // Expand a whole-stockpile imported limit into explicit per-def entries over the unit's
        // currently-allowed defs (PSC has no whole-unit default — every limit is per-def). A per-def
        // upper already written from an exact source (Stack Gap's allowedPerItem) is kept; the
        // imported default only fills gaps. The lower threshold applies to every allowed def, clamped
        // to that def's upper. Future-allowed defs are not covered — the player can re-apply via the
        // control window's "Apply to all allowed".
        private static void ApplyDefaultToAllowed(StorageSettings settings, PscStorageData data, int? lower, int? upper)
        {
            if ((lower == null && upper == null) || settings?.filter == null) return;
            foreach (var def in settings.filter.AllowedThingDefs)
            {
                if (def == null) continue;
                if (!data.limits.TryGetValue(def, out var lim) || lim == null)
                {
                    lim = new PscDefLimit();
                    data.limits[def] = lim;
                }
                if (upper.HasValue && !lim.Upper.HasValue) lim.Upper = upper.Value;
                if (lower.HasValue && !lim.Lower.HasValue)
                {
                    int l = lower.Value;
                    if (lim.Upper.HasValue && l > lim.Upper.Value) l = lim.Upper.Value;
                    lim.Lower = l;
                }
            }
        }

        // ── capacity / basis helpers (off any hot path — runs once per migrated unit, at load) ────
        private static void Capacity(PscHaulUnit unit, out int cellCount, out int slots)
        {
            cellCount = 0;
            slots = 0;
            var map = unit.Map;
            var cells = unit.group?.CellsList;   // static temp list — iterate now, never retain
            if (cells == null) return;
            cellCount = cells.Count;
            for (int i = 0; i < cells.Count; i++)
                slots += map != null ? cells[i].GetMaxItemsAllowedInCell(map) : 1;
            if (slots < cellCount) slots = cellCount;
        }

        private static int CapacityItems(PscHaulUnit unit, StorageSettings settings)
        {
            Capacity(unit, out _, out int slots);
            return Mathf.Max(1, slots) * MaxStackBasis(settings);
        }

        // Largest stackLimit among the unit's currently-allowed defs (fallback 75 — vanilla's common
        // max). Used only as a basis for percentage->item-count conversions, which are approximate.
        private static int MaxStackBasis(StorageSettings settings)
        {
            int max = 0;
            var filter = settings?.filter;
            if (filter != null)
            {
                foreach (var d in filter.AllowedThingDefs)
                    if (d != null && d.stackLimit > max) max = d.stackLimit;
            }
            return max > 0 ? max : 75;
        }

        // Element-only children (skips any whitespace/text nodes so parallel keys/values stay aligned).
        private static List<XmlNode> ChildElements(XmlNode node)
        {
            var list = new List<XmlNode>();
            if (node != null)
                foreach (XmlNode c in node.ChildNodes)
                    if (c.NodeType == XmlNodeType.Element) list.Add(c);
            return list;
        }

        private static int IntNode(XmlNode parent, string name, int fallback)
        {
            var n = parent?[name];
            return n != null && int.TryParse(n.InnerText, out int v) ? v : fallback;
        }

        private static float FloatNode(XmlNode parent, string name, float fallback)
        {
            var n = parent?[name];
            return n != null && float.TryParse(n.InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                ? v : fallback;
        }

        // Parse a Verse.FloatRange text node ("min~max").
        private static bool TryParseRange(XmlNode parent, string name, out float min, out float max)
        {
            min = 0f; max = 1f;
            var text = parent?[name]?.InnerText;
            if (string.IsNullOrEmpty(text)) return false;
            int tilde = text.IndexOf('~');
            if (tilde < 0) return false;
            return float.TryParse(text.Substring(0, tilde), NumberStyles.Float, CultureInfo.InvariantCulture, out min)
                & float.TryParse(text.Substring(tilde + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out max);
        }

        // ── player notification ───────────────────────────────────────────────────────────────────
        private static void Announce(Dictionary<SourceKind, int> imported, int failed)
        {
            int total = 0;
            foreach (var kv in imported) total += kv.Value;

            // Always log a one-line summary (ungated) so bug reports show it without dev mode.
            if (total > 0 || failed > 0)
            {
                var sb = new System.Text.StringBuilder("[PSC] migration: ");
                if (total > 0)
                {
                    var parts = new List<string>();
                    foreach (var kind in Priority)
                        if (imported.TryGetValue(kind, out int c)) parts.Add($"{c} from {RawName(kind)}");
                    sb.Append("imported limits on ").Append(total).Append(" storage area(s) (")
                      .Append(string.Join(", ", parts.ToArray())).Append(")");
                }
                if (failed > 0) sb.Append(total > 0 ? "; " : "").Append(failed).Append(" failed (left unlimited)");
                Log.Message(sb.ToString());
            }

            if (total == 0 && failed == 0) return;

            var body = new System.Text.StringBuilder();
            foreach (var kind in Priority)
                if (imported.TryGetValue(kind, out int c))
                    body.AppendLine("PSC_MigrationImportedLine".Translate(DisplayName(kind), c));
            if (total > 0)
            {
                body.AppendLine();
                body.AppendLine("PSC_MigrationCaveat".Translate());
            }
            if (failed > 0)
            {
                body.AppendLine();
                body.AppendLine("PSC_MigrationFailed".Translate(failed));
            }

            try
            {
                Find.LetterStack?.ReceiveLetter("PSC_MigrationLetterLabel".Translate(),
                    body.ToString().TrimEndNewlines(),
                    failed > 0 && total == 0 ? LetterDefOf.NegativeEvent : LetterDefOf.NeutralEvent);
            }
            catch (Exception ex)
            {
                Log.Warning("[PSC] migration: could not post the summary letter. " + ex);
            }
        }

        private static string GateWord(bool enabled) => enabled ? "import" : "loaded(skip)";

        // Raw English name for logs (no translation dependency).
        private static string RawName(SourceKind kind)
        {
            switch (kind)
            {
                case SourceKind.StackGap: return "Stack Gap";
                case SourceKind.SatisfiedStorage: return "Satisfied Storage";
                case SourceKind.VarietyMatters: return "Variety Matters Stockpile";
                default: return "?";
            }
        }

        private static string DisplayName(SourceKind kind)
        {
            switch (kind)
            {
                case SourceKind.StackGap: return "PSC_MigrationModStackGap".Translate();
                case SourceKind.SatisfiedStorage: return "PSC_MigrationModSatisfiedStorage".Translate();
                case SourceKind.VarietyMatters: return "PSC_MigrationModVarietyMatters".Translate();
                default: return "?";
            }
        }
    }
}
