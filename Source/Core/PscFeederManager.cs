using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Owns one map's feeder-link graph (PscFeederLinks) plus the link mutation/query surface that was
    // previously inline on PscMapComponent. The component delegates its public feeder API here and
    // scribes the graph via ExposeData. After a graph change that can orphan strictness flags, the
    // manager pushes a tracking/gate refresh back to the owner component (RebuildTrackingFromStore).
    public class PscFeederManager
    {
        private readonly Map map;
        private readonly PscMapComponent owner;
        private PscFeederLinks links = new PscFeederLinks();

        public PscFeederManager(Map map, PscMapComponent owner)
        {
            this.map = map;
            this.owner = owner;
        }

        public PscFeederLinks Links => links;
        public bool IsEmpty => links.IsEmpty;

        // Scribed by the owner component under the map node. Write nothing when there are no links, so
        // adding PSC to a save (or never using feeders) leaves the map node untouched; an absent node
        // leaves the field at its default on load.
        public void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving && links.IsEmpty) return;
            Scribe_Deep.Look(ref links, "feederLinks");
            if (Scribe.mode == LoadSaveMode.PostLoadInit && links == null)
                links = new PscFeederLinks();
        }

        // ---- feeder link mutation (called from gizmos / lifecycle patches) ----

        // Create a directed link source -> dest. Returns true only when a NEW edge was created, so callers
        // (the link designator) can run one-shot follow-ups (auto-priority, strictness seeding) without
        // re-firing on a re-painted, already-linked unit. Strictness seeding is NOT done here — it is the
        // designator's job AFTER auto-priority, via SeedFeederStrictnessIfFunctional, so a route that
        // auto-priority makes functional gets seeded and a still-dead route does not (F4).
        //
        // A pair carries at most ONE direction: setting s -> d first drops any existing reverse d -> s,
        // so linking each of two piles to the other flips the direction instead of forming a
        // contradictory A<->B 2-cycle. Dropping the reverse can orphan a strict flag it had seeded, so
        // we prune afterwards (only when a reverse was actually removed).
        public bool AddFeederLink(PscHaulUnit source, PscHaulUnit dest)
        {
            if (!source.IsValid || !dest.IsValid) return false;
            string s = source.UniqueLoadID, d = dest.UniqueLoadID;
            if (s == null || d == null || s == d) return false;

            bool replacedReverse = links.RemoveEdge(d, s);
            if (replacedReverse) PscLog.Msg($"link: dropped reverse edge {d} -> {s} (replaced by {s} -> {d})");

            bool created = links.AddEdge(s, d);
            PscLog.Msg(created ? $"link: created {s} -> {d}" : $"link: edge already exists {s} -> {d}");

            if (replacedReverse) PruneFeederLinksAndFlags();
            return created;
        }

        // Seed the default pull/push strictness flags for a freshly created route — but ONLY when the route
        // is actually FUNCTIONAL (dest outranks source). Seeding strict flags onto a DEAD route (same- or
        // reverse-priority, auto-priority off) would lock both piles: the source's onlyToDestinations stops
        // it draining elsewhere and the dest's onlyFromSource stops it accepting elsewhere, while no item
        // can legally flow the route (F4). The designator calls this AFTER auto-priority, so a nudge that
        // makes the route functional still gets seeded. Each flag seeds only on the unit's FIRST
        // source/destination (count == 1 post-add) so a later route never re-enables a flag the player
        // turned off. Gizmo-only (clipboard/lifecycle paths add edges directly and never seed defaults).
        public void SeedFeederStrictnessIfFunctional(PscHaulUnit source, PscHaulUnit dest)
        {
            if (PscMod.Settings == null) return;
            if (!HasFunctionalFeederEdge(source, dest)) return;
            string s = source.UniqueLoadID, d = dest.UniqueLoadID;
            if (s == null || d == null) return;
            if (PscMod.Settings.defaultOnlyFromSource && links.SourceCount(d) == 1)
            {
                SetFeederFlag(dest.Settings, fromSource: true);
                PscLog.Msg($"link: seeded onlyFromSource on {d}");
            }
            if (PscMod.Settings.defaultOnlyToDestinations && links.DestinationCount(s) == 1)
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
            PscMapComponent.NotifyPolicyChanged(settings);   // updates tracking + anyFeederActive
        }

        // Break any connection between two units (either direction). Only edges touching `self` are
        // removed, so the targeted unit's other links are left alone. No-op when there is no edge.
        public void BreakFeederLink(PscHaulUnit self, PscHaulUnit other)
        {
            if (!self.IsValid || !other.IsValid) return;
            string a = self.UniqueLoadID, b = other.UniqueLoadID;
            if (a == null || b == null || a == b) return;
            bool removed = links.RemoveEdge(a, b);
            removed |= links.RemoveEdge(b, a);
            if (removed) { PscLog.Msg($"link: broke {a} <-> {b}"); PruneFeederLinksAndFlags(); }
        }

        public void ClearAllFeederLinks()
        {
            PscLog.Msg("link: clearing all feeder links on map");
            links.ClearAll();
            PruneFeederLinksAndFlags();
        }

        // Clear every feeder link touching one unit (the per-stockpile "clear" right-click option).
        // Distinct from RemoveFeederEndpoint, which is the despawn/deletion path.
        public void ClearFeederLinksFor(PscHaulUnit unit)
        {
            string id = unit.UniqueLoadID;
            if (id == null) return;
            if (links.RemoveAllFor(id)) { PscLog.Msg($"link: cleared all links for {id}"); PruneFeederLinksAndFlags(); }
        }

        // Copy/paste "duplicate" (replace semantics): the pasted-onto unit adopts the copied unit's
        // source and destination lists.
        public void ApplyClipboardLinks(PscHaulUnit unit, List<string> sources, List<string> dests)
        {
            string id = unit.UniqueLoadID;
            if (id == null) return;
            PscLog.Msg($"link: applying clipboard links to {id} ({sources?.Count ?? 0} src, {dests?.Count ?? 0} dst)");
            var liveIds = BuildLiveIds();
            links.RemoveAllFor(id);
            if (sources != null)
            {
                foreach (var s in sources)
                    if (liveIds.Contains(s)) links.AddEdge(s, id);
            }
            if (dests != null)
            {
                foreach (var d in dests)
                    if (liveIds.Contains(d)) links.AddEdge(id, d);
            }
            PruneFeederLinksAndFlags();
        }

        public void RemoveFeederEndpoint(string id)
        {
            if (id == null) return;
            PscLog.Msg($"link: removing endpoint {id} (storage despawn/delete)");
            links.RemoveAllFor(id);
            PscFeederHaulContext.ClearForEndpoint(id);
            PruneFeederLinksAndFlags();
        }

        public void PruneFeederLinksAndFlags(bool markDirty = false)
        {
            var liveIds = BuildLiveIds();
            int before = PscLog.Enabled ? links.Links.Count : 0;
            links.PruneToLiveIds(liveIds);
            if (PscLog.Enabled)
            {
                int removed = before - links.Links.Count;
                if (removed > 0) PscLog.Msg($"link: pruned {removed} dead edge(s)");
            }
            ClearOrphanedFeederFlags(liveIds);
            PscFeederHaulContext.PruneForMap(map, owner);
            owner.RebuildTrackingFromStore(markDirty);
            // A prune is exactly when storage units may have gone dead (despawn, unlink, removed mod);
            // drop the id cache so dead group objects don't linger as keys. Rebuilt lazily and cheaply;
            // prune is a rare UI/lifecycle event, never on the haul hot path.
            PscHaulUnit.ClearIdCache();
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

                if (data.onlyFromSource && !links.HasAnySource(id))
                    data.onlyFromSource = false;
                if (data.onlyToDestinations && !links.HasAnyDestination(id))
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

        // ---- queries ----

        public bool HasFunctionalFeederEdge(PscHaulUnit source, PscHaulUnit dest)
        {
            if (!source.IsValid || !dest.IsValid) return false;
            if (source.Map != map || dest.Map != map) return false;
            // Structural edge check FIRST: most (source, candidate-target) pairs in a haul scan have
            // no edge, so the cheap HasEdge lookup short-circuits before the priority comparison
            // (Outranks reads two fine-order ranks) and the settings reads. All conditions are ANDed;
            // reordering is behavior-neutral.
            if (!links.HasEdge(source.UniqueLoadID, dest.UniqueLoadID)) return false;
            var sourceSettings = source.Settings;
            var destSettings = dest.Settings;
            if (sourceSettings == null || destSettings == null) return false;
            // D5 unified onto the fine-order key (M4): the destination must strictly outrank the
            // source by full priority (band, then sub-tier, then letter), not just by vanilla band.
            return PscOrder.Outranks(destSettings, sourceSettings);
        }

        public bool HasFunctionalFeederEdge(string sourceId, string destId)
        {
            if (!TryResolveLiveUnit(sourceId, out var source)) return false;
            if (!TryResolveLiveUnit(destId, out var dest)) return false;
            return HasFunctionalFeederEdge(source, dest);
        }

        // Skip-hops variant: like HasFunctionalFeederEdge but accepts a multi-hop functional path, not
        // just a direct edge. Each functional hop outranks its source, so dest outranks source by
        // transitivity; the live Outranks check here keeps a later priority edit from leaving a stale
        // allowance (the structural reachability cache is priority-agnostic).
        public bool HasFunctionalFeederPath(PscHaulUnit source, PscHaulUnit dest)
        {
            if (!source.IsValid || !dest.IsValid) return false;
            if (source.Map != map || dest.Map != map) return false;
            // Structural reachability FIRST (memoised behind the graph generation): skip the Outranks
            // rank reads and settings reads on the common no-path pair. The live Outranks check still
            // runs for an actual path, keeping a later priority edit from leaving a stale allowance.
            if (!links.IsDownstreamReachable(source.UniqueLoadID, dest.UniqueLoadID)) return false;
            var sourceSettings = source.Settings;
            var destSettings = dest.Settings;
            if (sourceSettings == null || destSettings == null) return false;
            return PscOrder.Outranks(destSettings, sourceSettings);
        }

        public bool HasFunctionalFeederPath(string sourceId, string destId)
        {
            if (!TryResolveLiveUnit(sourceId, out var source)) return false;
            if (!TryResolveLiveUnit(destId, out var dest)) return false;
            return HasFunctionalFeederPath(source, dest);
        }

        // Unified admission predicate. A direct functional edge always carries; when the skip-hops
        // setting is on, a multi-hop functional path does too. Direct edge stays first as the cheap
        // fast path so the BFS only runs when skip is enabled and there is no direct edge.
        public bool FeederAllows(PscHaulUnit source, PscHaulUnit dest)
            => HasFunctionalFeederEdge(source, dest)
               || (PscMod.Settings != null && PscMod.Settings.feederSkipHops && HasFunctionalFeederPath(source, dest));

        public bool FeederAllows(string sourceId, string destId)
            => HasFunctionalFeederEdge(sourceId, destId)
               || (PscMod.Settings != null && PscMod.Settings.feederSkipHops && HasFunctionalFeederPath(sourceId, destId));

        // Loose-item skip rule (feederSkipLooseItems). A ground item has no source, so it is normally
        // barred from any onlyFromSource node. With this on, it may enter `dest` if the chain feeding
        // dest has an OPEN MOUTH: some upstream node that accepts the item's def and is NOT itself
        // "Pull only from sources". That node is where a ground item would naturally enter the chain;
        // the best-priority store search then carries it straight down to dest. A closed chain (no such
        // entry) still rejects ground items. Single O(groups) pass; only reached for loose items hitting
        // a chain destination when both toggles are on.
        public bool LooseItemMayEnterChainAt(PscHaulUnit dest, Thing t)
        {
            if (!dest.IsValid || t?.def == null) return false;
            var ancestors = links.UpstreamReachableFrom(dest.UniqueLoadID);
            if (ancestors.Count == 0) return false;   // no source path -> not a chain destination
            var groups = map.haulDestinationManager.AllGroupsListForReading;
            for (int i = 0; i < groups.Count; i++)
            {
                var u = PscHaulUnit.FromSlotGroup(groups[i]);
                if (!u.IsValid) continue;
                string id = u.UniqueLoadID;
                if (id == null || !ancestors.Contains(id)) continue;
                var settings = u.Settings;
                if (settings == null) continue;
                var adata = PscStorageDataStore.TryGet(settings);
                if (adata != null && adata.onlyFromSource) continue;   // not an open mouth
                if (settings.filter != null && settings.filter.Allows(t.def)) return true;
            }
            return false;
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
    }
}
