using System.Collections.Generic;
using Verse;

namespace PrecisionStockpileControl
{
    // Runtime-only feeder-source provenance for items a bulk inventory-hauler (Pick Up And Haul or Hauler's
    // Dream) has gathered into a pawn's inventory. Both gather via Thing.SplitOff + inventory.TryAdd(merge),
    // which (a) never touches Pawn_CarryTracker.TryStartCarry, so PscFeederHaulContext's per-Thing route never
    // transfers, and (b) MERGES same-def stacks, so per-Thing identity is destroyed. By unload the item is
    // unspawned with no source, and the feeder gate can neither admit it into an onlyFromSource chain node
    // nor hold it out of an unconstrained overflow (DESIGN: bulk inventory hauls are a soft edge).
    //
    // This restores that provenance at a COARSE granularity the merge can't defeat: per (pawn, def) COUNT
    // SEGMENTS, each remembering only the origin's id (everything strict is re-read LIVE at restore, so a
    // mid-haul link break or flag toggle is respected). Captured at the bulk-hauler's job commit while the
    // items are still spawned in their source; consumed by reconciling each segment's remaining count against the pawn's LIVE
    // inventory count of that def, so it drains across every unload exit (cell place, container deposit,
    // drop-on-ground) without a fragile per-placement hook. Imperfect by design (a merged stack carries one
    // source per placement; leftovers shake out via normal hauling), the accepted trade for keeping bulk.
    //
    // Lifecycle: runtime-only, NOT scribed; cleared on every new-game/load by PscGameComponent's ctor. Keyed
    // by the live Pawn so the periodic Sweep (PscGameComponent.GameComponentTick) can reconcile against its
    // inventory and drop emptied/dead entries even when no further store search looks the def up again. The
    // pin is harmless: entries exist only during an active feeder haul (minutes) and the sweep + clear-on-load
    // bound it.
    public static class PscCarriedSourceTracker
    {
        // One captured run of `remaining` items of a def, gathered from feeder source `sourceId`. FIFO:
        // oldest segment drains first. No strictness snapshot — the restore reads the origin's live flags.
        private struct Segment
        {
            public Map map;
            public string sourceId;
            public int remaining;
            public int createdTick;
        }

        // pawn -> (def -> FIFO segment list).
        private static readonly Dictionary<Pawn, Dictionary<ThingDef, List<Segment>>> byPawn =
            new Dictionary<Pawn, Dictionary<ThingDef, List<Segment>>>();

        // pawn -> the loadID of the last bulk-gather job we captured. The hauler's TryMakePreToilReservations is
        // NOT a one-shot seam (it can fire more than once for the same job), and each fire re-walks the whole
        // pickup queue; without this guard the same pickup is recorded as several segments. Reconcile keeps the
        // COUNTS honest regardless, but the duplicates inflate the FIFO list and the log, and can skew which
        // source the oldest-segment lookup returns when stacks from two sources interleave. Capped (a lost
        // marker only permits a harmless re-capture); cleaned alongside byPawn.
        private static readonly Dictionary<Pawn, int> lastCapturedJob = new Dictionary<Pawn, int>();

        private const int MaxRecordAgeTicks = 120_000;   // ~2 in-game days: a bulk haul completes in minutes
        private const int SweepIntervalTicks = 1000;     // periodic reconcile/age-prune cadence
        private static int lastSweepTick = -1;

        // The cheapest gate for the carried-item restore path: with nothing captured there is nothing to
        // restore, so the feeder gate (and the tick sweep) skip all per-pawn work.
        public static bool IsEmpty => byPawn.Count == 0;

        // Cleared on every new-game / load by PscGameComponent's ctor, mirroring PscFeederHaulContext.
        public static void ClearAll() { byPawn.Clear(); lastCapturedJob.Clear(); lastSweepTick = -1; }

        // True when this pawn's gather job was already captured (a re-fired TryMakePreToilReservations), so the
        // caller skips re-recording it. Records the job otherwise. Bounded clear: dropping a marker only allows
        // a harmless re-capture, never a wrong count (reconcile trims to live inventory).
        public static bool AlreadyCapturedJob(Pawn pawn, int jobLoadId)
        {
            if (pawn == null) return false;
            if (lastCapturedJob.TryGetValue(pawn, out var prev) && prev == jobLoadId) return true;
            if (lastCapturedJob.Count > 256) lastCapturedJob.Clear();
            lastCapturedJob[pawn] = jobLoadId;
            return false;
        }

        // Record `count` items of `def` headed out of feeder source `sourceId` into the pawn's inventory.
        // Appends a FIFO segment. Strictness is intentionally NOT snapshotted (read live at restore).
        public static void Capture(Pawn pawn, ThingDef def, Map map, string sourceId, int count, int tick)
        {
            if (pawn == null || def == null || map == null || string.IsNullOrEmpty(sourceId) || count <= 0)
                return;

            if (!byPawn.TryGetValue(pawn, out var byDef))
                byPawn[pawn] = byDef = new Dictionary<ThingDef, List<Segment>>();
            if (!byDef.TryGetValue(def, out var segs))
                byDef[def] = segs = new List<Segment>(2);

            segs.Add(new Segment { map = map, sourceId = sourceId, remaining = count, createdTick = tick });
            if (PscLog.Enabled)
                PscLog.Msg($"feeder: bulk-ctx capture {def.defName} x{count} from {sourceId} for {pawn.LabelShort}");
        }

        // Resolve the effective feeder source id for a carried item of `def` in `carrier`'s inventory on
        // `map`. Reconciles the FIFO segments against the carrier's LIVE inventory count of the def (trimming
        // oldest-first to model already-placed items) and returns the oldest still-active segment's source.
        // Returns false when there is no captured provenance left (the item is a genuine leftover).
        public static bool TryGetSource(Pawn carrier, ThingDef def, Map map, out string sourceId)
        {
            sourceId = null;
            if (carrier == null || def == null || map == null) return false;
            if (!byPawn.TryGetValue(carrier, out var byDef)) return false;
            if (!byDef.TryGetValue(def, out var segs) || segs.Count == 0) return false;

            int inv = InventoryCount(carrier, def);
            ReconcileToInventory(segs, inv, map);
            if (segs.Count == 0)
            {
                byDef.Remove(def);
                if (byDef.Count == 0) byPawn.Remove(carrier);
                return false;
            }

            // Oldest still-active segment on this map wins (the whole carried stack routes per its source;
            // a second-source remainder routes once the first segment has drained, or shakes out).
            for (int i = 0; i < segs.Count; i++)
            {
                if (segs[i].map == map && segs[i].remaining > 0)
                {
                    sourceId = segs[i].sourceId;
                    return true;
                }
            }
            return false;
        }

        // Periodic reconcile + age-prune, driven by PscGameComponent.GameComponentTick. The inventory
        // reconcile is the primary drain (it catches the case codex flagged: after the final unload there is
        // no further store-search lookup for that def, so a lingering segment would otherwise survive until
        // age-prune and mis-route the same def next time the pawn carries it).
        public static void Tick(int now)
        {
            if (byPawn.Count == 0) return;
            if (lastSweepTick >= 0 && now - lastSweepTick < SweepIntervalTicks) return;
            lastSweepTick = now;
            Sweep(now);
        }

        private static void Sweep(int now)
        {
            List<Pawn> deadPawns = null;
            foreach (var kvP in byPawn)
            {
                var pawn = kvP.Key;
                var byDef = kvP.Value;
                if (pawn == null || pawn.Destroyed)
                {
                    (deadPawns ??= new List<Pawn>()).Add(pawn);
                    continue;
                }

                List<ThingDef> deadDefs = null;
                foreach (var kvD in byDef)
                {
                    var segs = kvD.Value;
                    segs.RemoveAll(s => now - s.createdTick > MaxRecordAgeTicks);   // age backstop
                    if (segs.Count > 0)
                        ReconcileToInventory(segs, InventoryCount(pawn, kvD.Key), null);   // map=null: all maps
                    if (segs.Count == 0) (deadDefs ??= new List<ThingDef>()).Add(kvD.Key);
                }
                if (deadDefs != null)
                    foreach (var d in deadDefs) byDef.Remove(d);
                if (byDef.Count == 0) (deadPawns ??= new List<Pawn>()).Add(pawn);
            }
            if (deadPawns != null)
                foreach (var p in deadPawns) { byPawn.Remove(p); lastCapturedJob.Remove(p); }
        }

        // Trim the captured total down to what the pawn actually still carries, oldest-first: anything beyond
        // the live inventory count has already been placed (across whatever unload exit). `map` scopes the
        // total to one map's segments (null = all maps, used by the sweep); inventory is map-agnostic.
        private static void ReconcileToInventory(List<Segment> segs, int inv, Map map)
        {
            int total = 0;
            for (int i = 0; i < segs.Count; i++)
                if (map == null || segs[i].map == map) total += segs[i].remaining;
            int over = total - inv;
            for (int i = 0; i < segs.Count && over > 0; i++)
            {
                if (map != null && segs[i].map != map) continue;
                var s = segs[i];
                int cut = s.remaining < over ? s.remaining : over;
                s.remaining -= cut;
                over -= cut;
                segs[i] = s;
            }
            segs.RemoveAll(s => s.remaining <= 0);
        }

        private static int InventoryCount(Pawn carrier, ThingDef def)
        {
            var c = carrier.inventory?.innerContainer;
            if (c == null) return 0;
            int n = 0;
            for (int i = 0; i < c.Count; i++)
                if (c[i].def == def) n += c[i].stackCount;
            return n;
        }
    }
}
