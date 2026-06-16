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
            RebuildDemand();
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
                        kv.Value.MarkAllDirty();
                    }
                }
            }
            anyPscActive = tracked.Count > 0;
            RebuildDemand();
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
