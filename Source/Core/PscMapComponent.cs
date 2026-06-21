using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Per-map runtime index. Auto-instantiated by RimWorld for every map (Map.FillComponents
    // reflects over MapComponent subclasses) — no Def needed.
    //
    // Holds: anyPscActive + the per-feature early-out gates (anyFeederActive / anyFineOrderActive /
    // anyFreezeModeActive / anyAlarmActive) and the staggered resync backstop. The feeder link graph
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

        // True when any tracked unit is in a freeze mode (Off / AcceptOnly). Gates the IsForbidden
        // freeze postfix (M5.2) — the hottest seam PSC touches — so a colony using no freeze mode
        // (incl. one using only RetrieveOnly) never pays its per-call slot-group resolution.
        public bool anyFreezeModeActive;

        // True when any tracked unit has an active stockpile alarm. Gates the alarm check pass in
        // MapComponentTick so a colony using no alarm pays nothing.
        public bool anyAlarmActive;

        // Authoritative directed feeder-link graph for this map (design §4.2) + its mutation/query
        // surface. Scribed via feeder.ExposeData below.
        private readonly PscFeederManager feeder;
        public PscFeederManager Feeder => feeder;
        public PscFeederLinks Links => feeder.Links;

        // Tracked = active StorageSettings whose owner resolves onto THIS map. Maintained on
        // policy change (runtime) and rebuilt in FinalizeInit (load).
        private readonly HashSet<StorageSettings> tracked = new HashSet<StorageSettings>();

        private readonly List<StorageSettings> resyncSnapshot = new List<StorageSettings>();
        private int resyncCursor;
        private const int ResyncInterval = 250;

        // Alarm check pass: every AlarmCheckInterval ticks, evaluate every tracked unit's alarm.
        // Fullness changes slowly, so a coarse interval is plenty. The snapshot list avoids mutating
        // `tracked` mid-iteration; the disabled list holds units whose alarm self-disabled (OneShot)
        // and need a deferred NotifyPolicyChanged after the loop.
        private readonly List<StorageSettings> alarmSnapshot = new List<StorageSettings>();
        private readonly List<StorageSettings> alarmDisabled = new List<StorageSettings>();
        private const int AlarmCheckInterval = 250;

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
            RecomputeFreezeModeActive();
            RecomputeAlarmActive();
        }

        private void RecomputeFeederActive()
            => RecomputeGate(ref anyFeederActive, "feeder: gate anyFeederActive",
                d => d.onlyFromSource || d.onlyToDestinations);

        public void RecomputeFineOrderActive()
            => RecomputeGate(ref anyFineOrderActive, "order: gate anyFineOrderActive",
                d => d.subTier != 0 || !string.IsNullOrEmpty(d.letter));

        private void RecomputeFreezeModeActive()
            => RecomputeGate(ref anyFreezeModeActive, "mode: gate anyFreezeModeActive",
                d => d.mode == PscStorageMode.Off || d.mode == PscStorageMode.AcceptOnly);

        private void RecomputeAlarmActive()
            => RecomputeGate(ref anyAlarmActive, "alarm: gate anyAlarmActive",
                d => d.alarm != null && d.alarm.IsActive);

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

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            // Drop feeder edges whose endpoints are no longer live storage on this map (removed
            // storage, removed mod, cross-map paste garbage) — self-heals each load.
            PruneFeederLinksAndFlags(markDirty: true);
        }

        // On map removal, drop the static references that would otherwise pin this dead map's object
        // graph alive until the next load: the For() memo, and any in-flight feeder routes on this map
        // (keyed by now-destroyed Things, holding this Map).
        public override void MapRemoved()
        {
            base.MapRemoved();
            if (ReferenceEquals(map, lastForMap)) { lastForMap = null; lastForComp = null; }
            PscFeederHaulContext.ClearForMap(map);
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
            RecomputeFreezeModeActive();
            RecomputeAlarmActive();
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

        // Per-frame world-space draw of the feeder route lines (Contagion pattern). Cheap early-outs
        // live inside the drawer (no links / nothing selected / overlay off).
        public override void MapComponentUpdate()
        {
            PscFeederOverlay.Draw(map, this);
        }

        // Screen-space draw of the storage-overlay panels (icons + priority). Separate seam from the
        // route lines: panels are OnGUI + zoom-gated, lines are world-space + all-zoom. The drawer
        // early-outs when the overlay toggle is off, so plain play pays nothing.
        public override void MapComponentOnGUI()
        {
            PscStorageOverlay.Draw(map);
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
            int tick = Find.TickManager.TicksGame;

            if (anyAlarmActive && tick % AlarmCheckInterval == 0)
                RunAlarmChecks(tick);

            if (tick % ResyncInterval != 0) return;
            resyncSnapshot.Clear();
            resyncSnapshot.AddRange(tracked);
            if (resyncSnapshot.Count == 0) return;
            if (resyncCursor >= resyncSnapshot.Count) resyncCursor = 0;
            PscStorageDataStore.TryGet(resyncSnapshot[resyncCursor++])?.MarkAllDirty();
        }

        // Evaluate every tracked unit's alarm against current fullness. Snapshot first because
        // Evaluate can self-disable a OneShot side, which mutates `tracked` via NotifyPolicyChanged;
        // apply those self-disables after the loop.
        private void RunAlarmChecks(int now)
        {
            alarmSnapshot.Clear();
            alarmSnapshot.AddRange(tracked);
            alarmDisabled.Clear();
            for (int i = 0; i < alarmSnapshot.Count; i++)
            {
                var s = alarmSnapshot[i];
                var data = PscStorageDataStore.TryGet(s);
                if (data?.alarm == null || !data.alarm.IsActive) continue;
                var unit = PscHaulUnit.ResolveSettings(s);
                if (!unit.IsValid) continue;
                if (PscAlarmRunner.Evaluate(s, data, unit, now))
                    alarmDisabled.Add(s);
            }
            for (int i = 0; i < alarmDisabled.Count; i++)
                NotifyPolicyChanged(alarmDisabled[i]);
        }

        // Right-click "disarm all alarms on this map": clear every tracked unit's alarm config.
        public void DisarmAllAlarms()
        {
            alarmSnapshot.Clear();
            alarmSnapshot.AddRange(tracked);
            for (int i = 0; i < alarmSnapshot.Count; i++)
            {
                var s = alarmSnapshot[i];
                var data = PscStorageDataStore.TryGet(s);
                if (data?.alarm == null) continue;
                data.alarm = null;
                NotifyPolicyChanged(s);
            }
        }
    }
}
