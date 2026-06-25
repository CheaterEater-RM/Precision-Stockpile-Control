using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

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

        // True when reservation-aware fill counting is ON and any tracked unit has an upper cap. Gates
        // the reserved-inbound rebuild pass so a colony with no maximums (or the setting off) pays nothing.
        public bool anyReservedActive;

        // True when the per-cell ("per-tile") master setting is ON and any tracked unit has a per-tile
        // cap. Gates the per-tile patches (PscPerTile) so a colony with the feature off, or on but with
        // no capped floor stockpile, pays only the cheap setting/IsEmpty checks before the cell lookup.
        public bool anyPerTileActive;

        // Authoritative directed feeder-link graph for this map (design §4.2) + its mutation/query
        // surface. Scribed via feeder.ExposeData below.
        private readonly PscFeederManager feeder;
        public PscFeederManager Feeder => feeder;
        public PscFeederLinks Links => feeder.Links;

        // Tracked = active StorageSettings whose owner resolves onto THIS map. Maintained on
        // policy change (runtime) and rebuilt in FinalizeInit (load). Internal so PscAdmissionIndex
        // can read it when rebuilding the prefilter below.
        internal readonly HashSet<StorageSettings> tracked = new HashSet<StorageSettings>();

        // Soft def->units "maybe-accepts" prefilter (store-search rewrite, Phase 1). Managed units whose
        // filter allows the def AND whose mode permits intake. Rebuilt from `tracked` on the same seams that
        // maintain the gates (UpdateTracking / RebuildTrackingFromStore / the resync backstop) via
        // PscAdmissionIndex.Rebuild; read via PscAdmissionIndex.CandidateUnits. Map-local, never persisted;
        // rebuilt in FinalizeInit on load. Built but not yet consumed until the engine lands (Phase 2/3).
        internal readonly Dictionary<ThingDef, List<StorageSettings>> admitIndex
            = new Dictionary<ThingDef, List<StorageSettings>>();

        private readonly List<StorageSettings> resyncSnapshot = new List<StorageSettings>();
        private int resyncCursor;
        private const int ResyncInterval = 250;

        // Reserved-inbound rebuild backstop: every ReservedRebuildInterval ticks, recompute every tracked
        // unit's reserved-inbound from the active player haul jobs (the split-counter's recompute-from-
        // truth). A single global pass — cheap (scans jobs, not contents), unlike the count resync above.
        private readonly List<StorageSettings> reservedSnapshot = new List<StorageSettings>();
        private const int ReservedRebuildInterval = 120;
        // Forces the next rebuild to do a full pawn-job scan even with nothing currently reserved, so hauls
        // already IN FLIGHT (with no Notify_Starting increment to re-arm them) are re-established after a
        // load, a setting toggle, or the first cap appearing. Set on the anyReservedActive false->true edge;
        // consumed by one rebuild. Otherwise a settled colony skips the scan entirely.
        private bool forceReservedRescan;

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
        // (TryFeederReject) and per store-search candidate (the PscStoreSearchEngine walk), and the vanilla
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
            // Wake vanilla's haulables lister for this unit's cells. A PSC-only policy edit (cap lowered,
            // freeze mode, feeder flags, batch) changes whether already-stored items are valid, but vanilla
            // only recalcs a slot group on its OWN filter/priority change — so without this poke the change
            // wouldn't take effect until ListerHaulablesTick's slow round-robin happens to revisit the cells.
            // settings.owner is non-null here (ResolveSettings required it). This is the exact seam vanilla
            // uses for filter edits (StorageSettings.TryNotifyChanged -> owner.Notify_SettingsChanged ->
            // listerHaulables.Notify_SlotGroupChanged); it does not call back into PSC, so no re-entrancy.
            settings.owner?.Notify_SettingsChanged();
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
            RecomputeReservedActive();
            RecomputePerTileActive();
            PscAdmissionIndex.Rebuild(this);
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

        // anyReservedActive folds in the GLOBAL setting (not just a per-unit predicate), so it can't use
        // RecomputeGate directly. ON only when the feature is enabled AND some tracked unit has a maximum.
        public void RecomputeReservedActive()
        {
            bool any = false;
            if (PscMod.Settings.reservedFillCounting)
            {
                foreach (var s in tracked)
                {
                    var d = PscStorageDataStore.TryGet(s);
                    if (d != null && d.HasAnyUpperLimit) { any = true; break; }
                }
            }
            if (PscLog.Enabled && any != anyReservedActive)
                PscLog.Msg($"reserve: gate anyReservedActive {anyReservedActive} -> {any}");
            // Newly active (load / toggle-on / first capped unit): force one full rebuild scan so hauls
            // already in flight get reservations even though their Notify_Starting fired before we tracked.
            if (any && !anyReservedActive) forceReservedRescan = true;
            // Newly INactive (last upper cap removed at runtime): the periodic rebuild is now gated off, so
            // clear any lingering reserved-inbound here or it would sit stale on the data object (N3). Benign
            // today — all readers gate on HasLimit+Upper — but defense-in-depth against a future read path.
            else if (!any && anyReservedActive) ClearAllReservedInbound();
            anyReservedActive = any;
        }

        // anyPerTileActive folds in the GLOBAL per-tile master setting (like anyReservedActive). ON only
        // when the feature is enabled AND some tracked unit has a per-tile cap. Called on every tracking
        // update and on the master-toggle flip (PscMod.RecomputePerTileActiveAllMaps).
        public void RecomputePerTileActive()
        {
            bool any = false;
            if (PscMod.Settings != null && PscMod.Settings.perTileLimits)
            {
                foreach (var s in tracked)
                {
                    var d = PscStorageDataStore.TryGet(s);
                    if (d != null && d.perTileLimit > 0) { any = true; break; }
                }
            }
            if (PscLog.Enabled && any != anyPerTileActive)
                PscLog.Msg($"perTile: gate anyPerTileActive {anyPerTileActive} -> {any}");
            anyPerTileActive = any;
        }

        // Called when the reservedFillCounting setting toggles (from PscMod). Refresh the gate and, when
        // turning the feature OFF, drop all reserved-inbound so no stale effective read lingers.
        public void RefreshReservedActive()
        {
            RecomputeReservedActive();
            if (!PscMod.Settings.reservedFillCounting)
                ClearAllReservedInbound();
        }

        private void ClearAllReservedInbound()
        {
            reservedSnapshot.Clear();
            reservedSnapshot.AddRange(tracked);
            for (int i = 0; i < reservedSnapshot.Count; i++)
                PscStorageDataStore.TryGet(reservedSnapshot[i])?.ClearReservedInbound();
        }

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
            // storage, removed mod, cross-map paste garbage) — self-heals each load. This also
            // rebuilds tracking and the per-feature gates, including anyFineOrderActive.
            PruneFeederLinksAndFlags(markDirty: true);
            // The fine-order sort tiebreak (HaulDestinationManager_Compare_Patch) gates on
            // anyFineOrderActive, but the spawn-time haul-destination sorts already ran band-only (the
            // flag is only established just above, after spawn). Re-sort now so the rank-primary
            // selection's sorted-best-first list assumption holds immediately on load, not only after
            // the first runtime storage change. Cheap (one InsertionSort) and only when fine-order is
            // actually in use. Runtime flag flips (NotifyOrderChanged, settings apply) already re-sort.
            if (anyFineOrderActive)
                map?.haulDestinationManager?.Notify_HaulDestinationChangedPriority();
        }

        // On map removal, drop the static references that would otherwise pin this dead map's object
        // graph alive until the next load: the For() memo, and any in-flight feeder routes on this map
        // (keyed by now-destroyed Things, holding this Map).
        public override void MapRemoved()
        {
            base.MapRemoved();
            if (ReferenceEquals(map, lastForMap)) { lastForMap = null; lastForComp = null; }
            PscFeederHaulContext.ClearForMap(map);
            PscHaulUnit.ClearIdCache();   // drop this map's group objects from the id cache

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
                        // markDirty == true only at load (FinalizeInit). Hysteresis state (lim.refill) is now
                        // PERSISTED on each limit, so the load path must NOT re-seed it from contents — that
                        // would clobber a deliberately-drained pile's Satisfied state back to Refilling. Just
                        // mark counts dirty; the first recompute's UpdateRefilling keeps the restored in-band
                        // state and reconciles only the rails. (Migration/paste still seed via
                        // Notify_LimitsSeeded — those are fresh policy, not a load-restore.)
                        if (markDirty) kv.Value.MarkAllDirty();
                    }
                }
            }
            anyPscActive = tracked.Count > 0;
            RecomputeFeederActive();
            RecomputeFineOrderActive();
            RecomputeFreezeModeActive();
            RecomputeAlarmActive();
            RecomputeReservedActive();
            RecomputePerTileActive();
            PscAdmissionIndex.Rebuild(this);
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
        public void SeedFeederStrictnessIfFunctional(PscHaulUnit source, PscHaulUnit dest) => feeder.SeedFeederStrictnessIfFunctional(source, dest);
        public void BreakFeederLink(PscHaulUnit self, PscHaulUnit other) => feeder.BreakFeederLink(self, other);
        public void ClearAllFeederLinks() => feeder.ClearAllFeederLinks();
        public void ClearFeederLinksFor(PscHaulUnit unit) => feeder.ClearFeederLinksFor(unit);
        public void ApplyClipboardLinks(PscHaulUnit unit, List<string> sources, List<string> dests)
            => feeder.ApplyClipboardLinks(unit, sources, dests);
        public void RemoveFeederEndpoint(string id) => feeder.RemoveFeederEndpoint(id);
        public void PruneFeederLinksAndFlags(bool markDirty = false) => feeder.PruneFeederLinksAndFlags(markDirty);
        public bool HasFunctionalFeederEdge(PscHaulUnit source, PscHaulUnit dest) => feeder.HasFunctionalFeederEdge(source, dest);
        public bool HasFunctionalFeederEdge(string sourceId, string destId) => feeder.HasFunctionalFeederEdge(sourceId, destId);
        public bool FeederAllows(PscHaulUnit source, PscHaulUnit dest) => feeder.FeederAllows(source, dest);
        public bool FeederAllows(string sourceId, string destId) => feeder.FeederAllows(sourceId, destId);
        public bool LooseItemMayEnterChainAt(PscHaulUnit dest, Thing t) => feeder.LooseItemMayEnterChainAt(dest, t);

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

            if (anyReservedActive && tick % ReservedRebuildInterval == 0)
                RebuildReservedInbound();

            if (tick % ResyncInterval != 0) return;
            // Soft staleness backstop for the def->units prefilter: catches changes that don't route through
            // UpdateTracking, most importantly a vanilla filter / category toggle on a managed unit. Bounded
            // by managed-unit count; runs once per ResyncInterval. (Filter-allows correctness never depends on
            // this: the live delegated AllowedToAccept enforces it. This only refreshes the soft prefilter.)
            PscAdmissionIndex.Rebuild(this);
            resyncSnapshot.Clear();
            resyncSnapshot.AddRange(tracked);
            if (resyncSnapshot.Count == 0) return;
            if (resyncCursor >= resyncSnapshot.Count) resyncCursor = 0;
            PscStorageDataStore.TryGet(resyncSnapshot[resyncCursor++])?.MarkAllDirty();
        }

        // Reserved-inbound rebuild backstop — the split-counter's recompute-from-truth (mirrors the count
        // cache recomputing from HeldThings, but the truth lives in active haul JOBS, not in the unit).
        // Clear-then-recompute (absolute, so double-registers / re-plans are wiped), then one global pass
        // over active player haul jobs. Corrects the drift the fast path (inc-at-build / dec-at-drop)
        // can't see: cancelled / interrupted / redirected hauls. O(spawned player pawns + queued jobs),
        // gated by anyReservedActive.
        private void RebuildReservedInbound()
        {
            // Clear every tracked unit's reserved and note whether ANY held a reservation coming in.
            reservedSnapshot.Clear();
            reservedSnapshot.AddRange(tracked);
            bool hadReserved = false;
            for (int i = 0; i < reservedSnapshot.Count; i++)
            {
                var d = PscStorageDataStore.TryGet(reservedSnapshot[i]);
                if (d == null || !d.HasAnyReserved()) continue;
                hadReserved = true;
                d.ClearReservedInbound();
            }

            // Settled colony: nothing reserved and no forced post-load/toggle pass => no drift to heal and
            // no live haul to (re)establish, so skip the whole pawn scan AND the log. A fresh haul re-arms
            // reserved via the Notify_Starting increment, which makes hadReserved true next interval.
            if (!hadReserved && !forceReservedRescan) return;
            forceReservedRescan = false;

            var pawns = map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
            bool accrued = false;
            for (int i = 0; i < pawns.Count; i++)
            {
                var jobs = pawns[i]?.jobs;
                if (jobs == null) continue;
                accrued |= AccrueHaulJob(pawns[i], jobs.curJob, isCurrent: true);
                var queue = jobs.jobQueue;
                if (queue != null)
                    for (int q = 0; q < queue.Count; q++)
                        accrued |= AccrueHaulJob(pawns[i], queue[q]?.job, isCurrent: false);
            }
            // Log only when the rebuild actually touched a reservation (incoming or re-accrued), so a steady
            // state never prints. Throttled regardless.
            if (PscLog.Enabled && (hadReserved || accrued))
                PscLog.MsgThrottled("reserve:rebuild", "reserve: rebuilt reserved-inbound from active jobs");
        }

        // Add one HaulToCell job's in-flight contribution to its target unit's reserved-inbound. Returns
        // true if it accrued anything (used to decide whether the rebuild had real work to log).
        private bool AccrueHaulJob(Pawn pawn, Job job, bool isCurrent)
        {
            if (job == null || job.def != JobDefOf.HaulToCell) return false;
            var unit = PscHaulUnit.ResolveCell(job.targetB.Cell, map);
            if (!unit.IsValid || !tracked.Contains(unit.Settings)) return false;
            var data = PscStorageDataStore.TryGet(unit.Settings);
            if (data == null) return false;

            ThingDef def;
            int amount;
            // Post-pickup, StartCarryThing(subtractNumTakenFromJobCount:true) zeroed job.count and
            // retargeted TargetA to the carried thing, so for the CURRENT job count the CARRIED stack —
            // the real in-flight amount, incl. opportunistic top-ups. Trusting job.count here would erase
            // a live reservation while the hauler walks. Queued / pre-pickup jobs use the planned count.
            var carried = isCurrent ? pawn.carryTracker?.CarriedThing : null;
            if (carried != null && job.haulMode == HaulMode.ToCellStorage)
            {
                def = carried.def;
                amount = carried.stackCount;
            }
            else
            {
                def = job.targetA.Thing?.def;
                amount = job.count;
            }
            if (def == null || amount <= 0 || !data.HasLimit(def)) return false;
            var lim = data.GetLimit(def);
            if (lim == null || !lim.Upper.HasValue) return false;
            data.AddReservedInbound(def, amount);
            return true;
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
