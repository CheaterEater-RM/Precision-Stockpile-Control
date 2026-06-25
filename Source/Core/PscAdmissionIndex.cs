using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // The store-search engine's read path (store-search rewrite, Phase 1).
    //
    // DELIBERATELY NOT a mirrored "hard record": mode / feeder participation / cap presence / fine-order
    // rank are already O(1) on PscStorageData and already invalidated by the existing seams
    // (NotifyPolicyChanged -> UpdateTracking, the tracked set, the anyXxxActive gates, the
    // NumberingGeneration-stamped rank cache that reads the band live, and the 250-tick resync). Duplicating
    // them into a second record would add an invalidation burden for no gain. Filter-allows is read LIVE (the
    // delegated vanilla AllowedToAccept enforces it during the engine's per-unit cell probe), never
    // summarized here. So the only state this owns is the SOFT def->units prefilter, held map-local on
    // PscMapComponent.admitIndex.
    //
    // The shared hard-admit predicate the engine and the reworked AllowedToAccept backstop will both call is
    // extracted here in Phase 2 (Task 2.3), when the backstop is reworked. Phase 1 is purely additive:
    // nothing reads this facade yet, so hauling behavior is unchanged.
    public static class PscAdmissionIndex
    {
        // ONE explicit rank source for the engine (do NOT trust AllGroupsListInPriorityOrder ordering as the
        // source of truth). PscOrder.RankWithinBand reads settings.Priority live, so a vanilla priority change
        // needs no extra invalidation. Lower = higher priority.
        public static int RankOf(StorageSettings s) => PscOrder.RankWithinBand(s);

        private static readonly List<StorageSettings> Empty = new List<StorageSettings>();

        // Soft "maybe-accepts" prefilter: the MANAGED units whose filter allows `def` AND whose mode permits
        // intake. Def-level only; item-specific filter facets (rot / quality / HP / special filters) are
        // confirmed live by the delegated vanilla AllowedToAccept, NEVER here. Unmanaged groups are
        // pure-vanilla and reached via the engine's walk of AllGroupsListInPriorityOrder, so they are not
        // listed. At Precise the engine ignores this and walks the full eligible set, so a stale list can
        // never drop an admissible unit; Balanced/Performance narrowing semantics are finalized in Phase 3.
        // Built but NOT yet authoritative in Phase 1. Treat the returned list as read-only.
        public static List<StorageSettings> CandidateUnits(Map map, ThingDef def)
        {
            if (def == null) return Empty;
            var psc = PscMapComponent.For(map);
            if (psc == null) return Empty;
            return psc.admitIndex.TryGetValue(def, out var list) ? list : Empty;
        }

        // Rebuild the map-local prefilter from the component's tracked set. Called from the existing tracking
        // seams (UpdateTracking, RebuildTrackingFromStore, the resync backstop), not on a new generation
        // counter. Bounded by (managed units x allowed defs per unit); a handful of managed units is trivial.
        internal static void Rebuild(PscMapComponent psc)
        {
            if (psc == null) return;
            var index = psc.admitIndex;
            index.Clear();
            foreach (var s in psc.tracked)
            {
                var data = PscStorageDataStore.TryGet(s);
                if (data == null) continue;
                // Off / RetrieveOnly block haul-in: such a unit can never be an intake candidate.
                if (data.mode != PscStorageMode.Normal && data.mode != PscStorageMode.AcceptOnly) continue;
                var filter = s.filter;
                if (filter == null) continue;
                foreach (var def in filter.AllowedThingDefs)
                {
                    if (def == null) continue;
                    if (!index.TryGetValue(def, out var list))
                    {
                        list = new List<StorageSettings>();
                        index[def] = list;
                    }
                    list.Add(s);
                }
            }
            if (PscLog.Enabled)
                PscLog.Msg($"index: rebuilt map={psc.map.uniqueID} units={psc.tracked.Count} defs={index.Count}");
        }
    }
}
