using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Per-map runtime index. Auto-instantiated by RimWorld for every map (Map.FillComponents
    // reflects over MapComponent subclasses) — no Def needed.
    //
    // Holds: anyPscActive (per-map early-out), the demand index (D17 groundwork — def -> tracked
    // units that have a rule for it), and the staggered resync backstop. The feeder link store
    // (M3) will also live here.
    public class PscMapComponent : MapComponent
    {
        public bool anyPscActive;

        // True when any tracked unit has a feeder flag (onlyFromSource / onlyToDestinations). Gates
        // the admission feeder block so plain colonies pay nothing for it.
        public bool anyFeederActive;

        // True when any tracked unit has a non-default fine-order key (subTier != 0 or a letter).
        // Gates the fine-order transpiler helper so plain colonies pay one bool check (design §9).
        public bool anyFineOrderActive;

        // Authoritative directed feeder-link store for this map (design §4.2). Scribed below.
        private PscFeederLinks feederLinks = new PscFeederLinks();
        public PscFeederLinks Links => feederLinks;

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

        public PscMapComponent(Map map) : base(map) { }

        public static PscMapComponent For(Map map) => map?.GetComponent<PscMapComponent>();

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
        {
            bool any = false;
            foreach (var s in tracked)
            {
                var d = PscStorageDataStore.TryGet(s);
                if (d != null && (d.onlyFromSource || d.onlyToDestinations)) { any = true; break; }
            }
            if (PscLog.Enabled && any != anyFeederActive)
                PscLog.Msg($"feeder: gate anyFeederActive {anyFeederActive} -> {any}");
            anyFeederActive = any;
        }

        public void RecomputeFineOrderActive()
        {
            bool any = false;
            foreach (var s in tracked)
            {
                var d = PscStorageDataStore.TryGet(s);
                if (d != null && (d.subTier != 0 || !string.IsNullOrEmpty(d.letter))) { any = true; break; }
            }
            if (PscLog.Enabled && any != anyFineOrderActive)
                PscLog.Msg($"order: gate anyFineOrderActive {anyFineOrderActive} -> {any}");
            anyFineOrderActive = any;
        }

        private void RebuildDemand()
        {
            demandIndex.Clear();
            foreach (var s in tracked)
            {
                var d = PscStorageDataStore.TryGet(s);
                if (d?.limits == null) continue;
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

        private void RebuildTrackingFromStore(bool markDirty)
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
            // Write nothing when there are no links, so adding PSC to a save (or never using feeders)
            // leaves the map node untouched. On load, an absent node leaves the field at its default.
            if (Scribe.mode == LoadSaveMode.Saving && feederLinks.IsEmpty) return;
            Scribe_Deep.Look(ref feederLinks, "feederLinks");
            if (Scribe.mode == LoadSaveMode.PostLoadInit && feederLinks == null)
                feederLinks = new PscFeederLinks();
        }

        // Per-frame world-space draw of the feeder overlay (Contagion pattern). Cheap early-outs
        // live inside the drawer (no links / nothing selected / overlay off).
        public override void MapComponentUpdate()
        {
            PscFeederOverlay.Draw(map, this);
        }

        // ---- feeder link mutation (called from gizmos / lifecycle patches) ----

        // Create a directed link source -> dest. On a unit acquiring its FIRST source/destination
        // (0->1), seed the matching strictness flag from the mod-setting default (D: default on).
        public void AddFeederLink(PscHaulUnit source, PscHaulUnit dest)
        {
            if (!source.IsValid || !dest.IsValid) return;
            string s = source.UniqueLoadID, d = dest.UniqueLoadID;
            if (s == null || d == null || s == d) return;

            bool destHadSource = feederLinks.HasAnySource(d);
            bool sourceHadDest = feederLinks.HasAnyDestination(s);
            if (!feederLinks.AddEdge(s, d)) { PscLog.Msg($"link: edge already exists {s} -> {d}"); return; }
            PscLog.Msg($"link: created {s} -> {d}");

            if (!destHadSource && PscMod.Settings.defaultOnlyFromSource)
            {
                SetFeederFlag(dest.Settings, fromSource: true);
                PscLog.Msg($"link: seeded onlyFromSource on {d}");
            }
            if (!sourceHadDest && PscMod.Settings.defaultOnlyToDestinations)
            {
                SetFeederFlag(source.Settings, fromSource: false);
                PscLog.Msg($"link: seeded onlyToDestinations on {s}");
            }
        }

        private static void SetFeederFlag(StorageSettings settings, bool fromSource)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            if (data == null) return;
            if (fromSource) data.onlyFromSource = true; else data.onlyToDestinations = true;
            NotifyPolicyChanged(settings);   // updates tracking + anyFeederActive
        }

        // Break any connection between two units (either direction). Only edges touching `self` are
        // removed, so the targeted unit's other links are left alone. No-op when there is no edge.
        public void BreakFeederLink(PscHaulUnit self, PscHaulUnit other)
        {
            if (!self.IsValid || !other.IsValid) return;
            string a = self.UniqueLoadID, b = other.UniqueLoadID;
            if (a == null || b == null || a == b) return;
            bool removed = feederLinks.RemoveEdge(a, b);
            removed |= feederLinks.RemoveEdge(b, a);
            if (removed) { PscLog.Msg($"link: broke {a} <-> {b}"); PruneFeederLinksAndFlags(); }
        }

        public void ClearAllFeederLinks()
        {
            PscLog.Msg("link: clearing all feeder links on map");
            feederLinks.ClearAll();
            PruneFeederLinksAndFlags();
        }

        // Clear every feeder link touching one unit (the per-stockpile "clear" right-click option).
        // Distinct from RemoveFeederEndpoint, which is the despawn/deletion path.
        public void ClearFeederLinksFor(PscHaulUnit unit)
        {
            string id = unit.UniqueLoadID;
            if (id == null) return;
            if (feederLinks.RemoveAllFor(id)) { PscLog.Msg($"link: cleared all links for {id}"); PruneFeederLinksAndFlags(); }
        }

        // Copy/paste "duplicate" (replace semantics): the pasted-onto unit adopts the copied unit's
        // source and destination lists.
        public void ApplyClipboardLinks(PscHaulUnit unit, List<string> sources, List<string> dests)
        {
            string id = unit.UniqueLoadID;
            if (id == null) return;
            PscLog.Msg($"link: applying clipboard links to {id} ({sources?.Count ?? 0} src, {dests?.Count ?? 0} dst)");
            var liveIds = BuildLiveIds();
            feederLinks.RemoveAllFor(id);
            if (sources != null)
            {
                foreach (var s in sources)
                    if (liveIds.Contains(s)) feederLinks.AddEdge(s, id);
            }
            if (dests != null)
            {
                foreach (var d in dests)
                    if (liveIds.Contains(d)) feederLinks.AddEdge(id, d);
            }
            PruneFeederLinksAndFlags();
        }

        public void RemoveFeederEndpoint(string id)
        {
            if (id == null) return;
            PscLog.Msg($"link: removing endpoint {id} (storage despawn/delete)");
            feederLinks.RemoveAllFor(id);
            PscFeederHaulContext.ClearForEndpoint(id);
            PruneFeederLinksAndFlags();
        }

        public void PruneFeederLinksAndFlags(bool markDirty = false)
        {
            var liveIds = BuildLiveIds();
            int before = PscLog.Enabled ? feederLinks.Links.Count : 0;
            feederLinks.PruneToLiveIds(liveIds);
            if (PscLog.Enabled)
            {
                int removed = before - feederLinks.Links.Count;
                if (removed > 0) PscLog.Msg($"link: pruned {removed} dead edge(s)");
            }
            ClearOrphanedFeederFlags(liveIds);
            PscFeederHaulContext.PruneForMap(map, this);
            RebuildTrackingFromStore(markDirty);
        }

        private void ClearOrphanedFeederFlags(HashSet<string> liveIds)
        {
            if (PscStorageDataStore.IsEmpty || liveIds == null) return;

            List<StorageSettings> remove = null;
            foreach (var kv in PscStorageDataStore.All)
            {
                var data = kv.Value;
                if (data == null || (!data.onlyFromSource && !data.onlyToDestinations)) continue;

                var unit = PscHaulUnit.ResolveSettings(kv.Key);
                if (!unit.IsValid || unit.Map != map) continue;
                string id = unit.UniqueLoadID;
                if (id == null || !liveIds.Contains(id)) continue;

                if (data.onlyFromSource && !feederLinks.HasAnySource(id))
                    data.onlyFromSource = false;
                if (data.onlyToDestinations && !feederLinks.HasAnyDestination(id))
                    data.onlyToDestinations = false;

                if (!data.HasPersistentPolicy)
                    (remove ??= new List<StorageSettings>()).Add(kv.Key);
            }

            if (remove != null)
            {
                foreach (var s in remove)
                    PscStorageDataStore.Remove(s);
            }
        }

        public bool HasFunctionalFeederEdge(PscHaulUnit source, PscHaulUnit dest)
        {
            if (!source.IsValid || !dest.IsValid) return false;
            if (source.Map != map || dest.Map != map) return false;
            var sourceSettings = source.Settings;
            var destSettings = dest.Settings;
            if (sourceSettings == null || destSettings == null) return false;
            // D5 unified onto the fine-order key (M4): the destination must strictly outrank the
            // source by full priority (band, then sub-tier, then letter), not just by vanilla band.
            if (!PscOrder.Outranks(destSettings, sourceSettings)) return false;
            return feederLinks.HasEdge(source.UniqueLoadID, dest.UniqueLoadID);
        }

        public bool HasFunctionalFeederEdge(string sourceId, string destId)
        {
            if (!TryResolveLiveUnit(sourceId, out var source)) return false;
            if (!TryResolveLiveUnit(destId, out var dest)) return false;
            return HasFunctionalFeederEdge(source, dest);
        }

        public bool TryResolveLiveUnit(string id, out PscHaulUnit unit)
        {
            unit = default;
            if (id == null) return false;
            var groups = map.haulDestinationManager.AllGroupsListForReading;
            for (int i = 0; i < groups.Count; i++)
            {
                var u = PscHaulUnit.FromSlotGroup(groups[i]);
                if (u.IsValid && u.UniqueLoadID == id)
                {
                    unit = u;
                    return true;
                }
            }
            return false;
        }

        // Every live storage unit's id on this map (canonical: StorageGroup when grouped).
        public HashSet<string> BuildLiveIds()
        {
            var set = new HashSet<string>();
            var groups = map.haulDestinationManager.AllGroupsListForReading;
            for (int i = 0; i < groups.Count; i++)
            {
                var id = PscHaulUnit.FromSlotGroup(groups[i]).UniqueLoadID;
                if (id != null) set.Add(id);
            }
            return set;
        }

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
