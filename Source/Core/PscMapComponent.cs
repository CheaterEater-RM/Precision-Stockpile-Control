using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Per-map runtime index. Auto-instantiated by RimWorld for every map (Map.FillComponents
    // reflects over MapComponent subclasses) — no Def needed.
    //
    // Holds: anyPscActive (per-map early-out), the demand index (D17 groundwork — def -> tracked
    // units that have a rule for it), and the staggered resync backstop. The feeder link graph (M3)
    // and its mutation/query surface live in PscFeederManager; this component is the facade for it.
    public class PscMapComponent : MapComponent
    {
        public bool anyPscActive;

        // True when any tracked unit has a feeder flag (onlyFromSource / onlyToDestinations). Gates
        // the admission feeder block so plain colonies pay nothing for it.
        public bool anyFeederActive;

        // True when any tracked unit has a non-default fine-order key (subTier != 0 or a letter).
        // Gates the fine-order transpiler helper so plain colonies pay one bool check (design §9).
        public bool anyFineOrderActive;

        // Authoritative directed feeder-link graph for this map (design §4.2) + its mutation/query
        // surface. Scribed via feeder.ExposeData below.
        private readonly PscFeederManager feeder;
        public PscFeederManager Feeder => feeder;
        public PscFeederLinks Links => feeder.Links;

        // Tracked = active StorageSettings whose owner resolves onto THIS map. Maintained on
        // policy change (runtime) and rebuilt in FinalizeInit (load).
        private readonly HashSet<StorageSettings> tracked = new HashSet<StorageSettings>();

        // Demand index (D17). M1 granularity: "has a non-default rule for this def". M3 will refine
        // it to "currently accepting". Not consulted on the M1 hot path (the global store lookup is
        // already the early-out) — built now so feeder routing has it ready.
        private readonly Dictionary<ThingDef, List<StorageSettings>> demandIndex
            = new Dictionary<ThingDef, List<StorageSettings>>();

        private readonly List<StorageSettings> resyncSnapshot = new List<StorageSettings>();
        private int resyncCursor;
        private const int ResyncInterval = 250;

        public PscMapComponent(Map map) : base(map)
        {
            feeder = new PscFeederManager(map, this);
        }

        // One-entry memo for the haul hot path. For() is called per admission candidate
        // (TryFeederReject) and per relocation candidate (ShouldContinueSearch), and the vanilla
        // Map.GetComponent<T> is a linear is-T scan over every map component (long with many mods
        // loaded). A map's PscMapComponent instance never changes for its lifetime, so caching the
        // last (map, component) pair is always valid for a live map. The single strong Map ref is
        // replaced the moment For() is called for a different live map (constant during play).
        private static Map lastForMap;
        private static PscMapComponent lastForComp;

        public static PscMapComponent For(Map map)
        {
            if (map == null) return null;
            if (ReferenceEquals(map, lastForMap)) return lastForComp;
            var comp = map.GetComponent<PscMapComponent>();
            lastForMap = map;
            lastForComp = comp;
            return comp;
        }

        // Central entry point after any policy edit (UI, paste, link/unlink). Resolves the owning
        // map and updates that map component's tracking + demand. Safe to call with clipboard or
        // unspawned settings (resolves to no map -> no-op).
        public static void NotifyPolicyChanged(StorageSettings settings)
        {
            var unit = PscHaulUnit.ResolveSettings(settings);
            if (!unit.IsValid) return;
            For(unit.Map)?.UpdateTracking(settings);
        }

        private void UpdateTracking(StorageSettings settings)
        {
            var data = PscStorageDataStore.TryGet(settings);
            bool active = data != null && data.HasPersistentPolicy;
            if (active)
            {
                tracked.Add(settings);
                data.MarkAllDirty();
            }
            else
            {
                tracked.Remove(settings);
                PscStorageDataStore.Remove(settings);
            }
            anyPscActive = tracked.Count > 0;
            RecomputeFeederActive();
            RecomputeFineOrderActive();
            RebuildDemand();
        }

        private void RecomputeFeederActive()
            => RecomputeGate(ref anyFeederActive, "feeder: gate anyFeederActive",
                d => d.onlyFromSource || d.onlyToDestinations);

        public void RecomputeFineOrderActive()
            => RecomputeGate(ref anyFineOrderActive, "order: gate anyFineOrderActive",
                d => d.subTier != 0 || !string.IsNullOrEmpty(d.letter));

        // Recompute a per-map early-out gate: true when any tracked unit's data satisfies `predicate`.
        // The predicate is a static lambda (no capture), so this allocates nothing per call.
        private void RecomputeGate(ref bool gate, string logTag, Func<PscStorageData, bool> predicate)
        {
            bool any = false;
            foreach (var s in tracked)
            {
                var d = PscStorageDataStore.TryGet(s);
                if (d != null && predicate(d)) { any = true; break; }
            }
            if (PscLog.Enabled && any != gate)
                PscLog.Msg($"{logTag} {gate} -> {any}");
            gate = any;
        }

        private void RebuildDemand()
        {
            demandIndex.Clear();
            foreach (var s in tracked)
            {
                var d = PscStorageDataStore.TryGet(s);
                if (d == null) continue;
                foreach (var kv in d.limits)
                {
                    if (kv.Key == null || kv.Value == null || kv.Value.IsDefault) continue;
                    if (!demandIndex.TryGetValue(kv.Key, out var list))
                    {
                        list = new List<StorageSettings>();
                        demandIndex[kv.Key] = list;
                    }
                    list.Add(s);
                }
            }
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            // Drop feeder edges whose endpoints are no longer live storage on this map (removed
            // storage, removed mod, cross-map paste garbage) — self-heals each load.
            PruneFeederLinksAndFlags(markDirty: true);
        }

        internal void RebuildTrackingFromStore(bool markDirty)
        {
            tracked.Clear();
            if (!PscStorageDataStore.IsEmpty)
            {
                foreach (var kv in PscStorageDataStore.All)
                {
                    if (!kv.Value.HasPersistentPolicy) continue;
                    var unit = PscHaulUnit.ResolveSettings(kv.Key);
                    if (unit.IsValid && unit.Map == map)
                    {
                        tracked.Add(kv.Key);
                        if (markDirty) kv.Value.MarkAllDirty();
                    }
                }
            }
            anyPscActive = tracked.Count > 0;
            RecomputeFeederActive();
            RecomputeFineOrderActive();
            RebuildDemand();
        }

        // Called after a fine-order edit (sub-tier / letter / band via the level box). Updates
        // tracking + anyFineOrderActive, then re-sorts the haul-destination list so the new key
        // takes effect (vanilla only re-sorts on this notify).
        public static void NotifyOrderChanged(StorageSettings settings)
        {
            NotifyPolicyChanged(settings);
            var unit = PscHaulUnit.ResolveSettings(settings);
            if (unit.IsValid)
                unit.Map?.haulDestinationManager?.Notify_HaulDestinationChangedPriority();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            feeder.ExposeData();
        }

        // Per-frame world-space draw of the feeder overlay (Contagion pattern). Cheap early-outs
        // live inside the drawer (no links / nothing selected / overlay off).
        public override void MapComponentUpdate()
        {
            PscFeederOverlay.Draw(map, this);
        }

        // ---- feeder facade: thin pass-throughs to PscFeederManager (logic lives there) ----

        public bool AddFeederLink(PscHaulUnit source, PscHaulUnit dest) => feeder.AddFeederLink(source, dest);
        public void BreakFeederLink(PscHaulUnit self, PscHaulUnit other) => feeder.BreakFeederLink(self, other);
        public void ClearAllFeederLinks() => feeder.ClearAllFeederLinks();
        public void ClearFeederLinksFor(PscHaulUnit unit) => feeder.ClearFeederLinksFor(unit);
        public void ApplyClipboardLinks(PscHaulUnit unit, List<string> sources, List<string> dests)
            => feeder.ApplyClipboardLinks(unit, sources, dests);
        public void RemoveFeederEndpoint(string id) => feeder.RemoveFeederEndpoint(id);
        public void PruneFeederLinksAndFlags(bool markDirty = false) => feeder.PruneFeederLinksAndFlags(markDirty);
        public bool HasFunctionalFeederEdge(PscHaulUnit source, PscHaulUnit dest) => feeder.HasFunctionalFeederEdge(source, dest);
        public bool HasFunctionalFeederEdge(string sourceId, string destId) => feeder.HasFunctionalFeederEdge(sourceId, destId);

        // Staggered resync backstop: every ResyncInterval ticks, mark one tracked unit fully dirty
        // so its counts are recomputed from HeldThings on the next read. Self-heals any drift
        // source not covered by an explicit patch. Not the primary mechanism — the drift-seam
        // patches are — just a safety net.
        public override void MapComponentTick()
        {
            if (tracked.Count == 0) return;
            if (Find.TickManager.TicksGame % ResyncInterval != 0) return;

            resyncSnapshot.Clear();
            resyncSnapshot.AddRange(tracked);
            if (resyncSnapshot.Count == 0) return;
            if (resyncCursor >= resyncSnapshot.Count) resyncCursor = 0;
            PscStorageDataStore.TryGet(resyncSnapshot[resyncCursor++])?.MarkAllDirty();
        }
    }
}
