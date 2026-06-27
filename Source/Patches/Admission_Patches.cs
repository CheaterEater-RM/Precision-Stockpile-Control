using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace PrecisionStockpileControl
{
    // Stockpile-wide admission gate (design §8). Postfix on the per-group AllowedToAccept(Thing)
    // — the overload TryFindBestBetterStoreCellForWorker calls once per candidate group, NOT the
    // ThingDef overload (which is the scan/UI path; leaving it untouched keeps defs visible in the
    // filter). TIGHTEN-ONLY: never flips a vanilla false to true.
    [HarmonyPatch(typeof(StorageSettings), nameof(StorageSettings.AllowedToAccept), new[] { typeof(Thing) })]
    public static class StorageSettings_AllowedToAccept_Patch
    {
        public static void Postfix(StorageSettings __instance, Thing t, ref bool __result)
        {
            // Engine-context bypass (store-search rewrite, Phase 2): during the engine's own delegated
            // …ForIn cell probe, admission was already decided by the engine's hard-admit, so this backstop
            // early-outs. The flag is set ONLY around that probe (PscStoreSearchEngine), so outside it this
            // backstop is fully active for every external caller.
            if (PscEngineScope.BypassAdmissionBackstop) return;
            if (!__result) return;                       // never override vanilla rejection
            if (PscStorageDataStore.IsEmpty) return;     // cheapest early-out
            if (t == null) return;

            // The backstop serves external callers: validity rechecks (JobDriver_HaulToCell FailOn,
            // IsInValidStorage / ListerHaulables, filter UI), PUAH / Hauler's Dream bulk admission, store-search
            // clones -- AND one PLANNING path: a store search the engine CEDED to vanilla/LWM for a DSU-resident
            // source item (PscEngineScope.VanillaFallbackPlanning). The two need opposite counts, so the planning
            // mode is the flag, not a constant:
            //   - flag false (every recheck): PHYSICAL counts. Reserved-inbound is the engine's concern; a
            //     recheck reading effective would make an in-flight hauler's own reservation self-cancel it.
            //   - flag true (DSU cede): EFFECTIVE/reserved counts, because a store search IS planning -- without
            //     it, concurrent haulers relocating out of Deep Storage all admit into the same capped unit
            //     against a stale physical count and overshoot the cap. Scoped to the ceded search only (set at
            //     the engine's DSU cede, cleared by StoreUtility_Engine_Patch.Finalizer), so it never reaches a
            //     FailOn recheck (which runs outside TryFindBestBetterStoreCellFor).
            var unit = PscHaulUnit.ResolveSettings(__instance);
            if (!unit.IsValid) return;

            // Map-local gate (consistent with the engine): no PSC activity on this unit's map means no PSC
            // rule can apply (source and target share a map for an intra-map haul), so behave exactly like
            // vanilla. The global IsEmpty above does not cover a multi-map game where another map is managed
            // but this one is not. For() is memoised, so this is cheap.
            var psc = PscMapComponent.For(unit.Map);
            if (psc == null || !psc.anyPscActive) return;

            var source = PscHaulUnit.ResolveCurrent(t);
            var sourceData = source.IsValid ? PscStorageDataStore.TryGet(source.Settings) : null;
            bool sourceIsTarget = source.IsValid && source.Equals(unit);

            if (PscAdmissionIndex.HardReject(__instance, t, unit, source, sourceData, sourceIsTarget,
                    planning: PscEngineScope.VanillaFallbackPlanning, out _))
                __result = false;
        }
    }

    // Filter-edit seam (store-search rewrite, Phase 1 1C). Vanilla wires the storage filter's change
    // callback to the PRIVATE StorageSettings.TryNotifyChanged (filter = new ThingFilter(TryNotifyChanged),
    // RimWorld/StorageSettings.cs), which fires on every player category/filter toggle. PSC's soft
    // def->units prefilter (PscMapComponent.admitIndex) is the one piece of state a vanilla filter edit
    // can stale WITHOUT routing through PSC's own NotifyPolicyChanged, so mark the owning map's index
    // dirty here; MapComponentTick then rebuilds it lazily (<=250 ticks) instead of unconditionally every
    // window. PSC's own edits call owner.Notify_SettingsChanged() directly (NOT TryNotifyChanged), so this
    // never self-triggers. Private target -> explicit TargetMethod. Cheap: filter edits are a rare UI event.
    [HarmonyPatch]
    public static class StorageSettings_TryNotifyChanged_AdmitIndexPatch
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(StorageSettings), "TryNotifyChanged");

        public static void Postfix(StorageSettings __instance)
        {
            if (PscStorageDataStore.IsEmpty) return;     // no PSC policy anywhere -> nothing to refresh
            var unit = PscHaulUnit.ResolveSettings(__instance);
            if (!unit.IsValid) return;
            PscMapComponent.For(unit.Map)?.MarkAdmitIndexDirty();
        }
    }

    // Haul-count upper clamp (design §7, §8). Postfix on the job builder: cap job.count so the trip
    // never plans to exceed the target maximum. Vanilla's own clamp point is
    // job.count = Mathf.Min(job.count, num); this runs after it.
    [HarmonyPatch(typeof(HaulAIUtility), nameof(HaulAIUtility.HaulToCellStorageJob),
        new[] { typeof(Pawn), typeof(Thing), typeof(IntVec3), typeof(bool) })]
    public static class HaulAIUtility_HaulToCellStorageJob_Patch
    {
        public static void Postfix(Pawn p, Thing t, IntVec3 storeCell, ref Job __result)
        {
            if (__result == null) return;
            if (PscStorageDataStore.IsEmpty) return;
            if (t == null || p?.Map == null) return;

            var slot = p.Map.haulDestinationManager.SlotGroupAt(storeCell);
            if (slot == null) return;
            ISlotGroup canon = slot.StorageGroup != null ? (ISlotGroup)slot.StorageGroup : slot;
            var targetUnit = new PscHaulUnit(canon);
            var sourceUnit = PscHaulUnit.ResolveCurrent(t);
            if (t.Spawned && t.Position == storeCell) { __result = null; PscFeederHaulContext.Clear(t); return; }

            // Per-cell spread (floor-only, opt-in): an over-cap floor cell's excess may relocate to an
            // emptier cell IN THE SAME stockpile, so the same-unit cancel below must NOT fire for that
            // genuine intra-unit spread. perTileSrcCap is the cap on t's current cell; > cap means excess.
            bool perTileSpread = PscPerTile.TryGetCellCap(t, out int perTileSrcCap) && t.stackCount > perTileSrcCap;

            if (!perTileSpread && sourceUnit.IsValid && sourceUnit.Equals(targetUnit)) { __result = null; PscFeederHaulContext.Clear(t); return; }

            var psc = PscMapComponent.For(p.Map);
            if (psc != null && sourceUnit.IsValid && psc.FeederAllows(sourceUnit, targetUnit))
            {
                // Disable opportunistic duplicates for a feeder haul (harmless on a discarded probe job).
                // The route Register itself is NOT done here (F3): this builder also runs during discarded
                // WorkGiver.HasJobOnThing feasibility probes, so registering here leaked stale routes that
                // could transfer onto a carried stack (under PUAH/HD) and wrong-reject a later delivery.
                // Registration now lives at the committed job-start seam (JobDriver_HaulToCell.Notify_Starting,
                // Feeder_HaulContext_Patches), mirroring the reserved-inbound fix.
                __result.haulOpportunisticDuplicates = false;
            }
            else
            {
                PscFeederHaulContext.Clear(t);
            }

            // Per-cell ("per-tile") clamp (floor-only, opt-in). Dest: never deliver more than the
            // destination cell's remaining room under its cap. Source: when the source cell is over its
            // cap, move only the excess so it lands exactly on the cap (anti-oscillation, mirrors the
            // per-def drain clamp). Composes by min with the per-def clamps below.
            if (PscPerTile.TryGetCellRoom(storeCell, p.Map, t.def, out int cellRoom))
            {
                if (cellRoom <= 0) { PscFeederHaulContext.Clear(t); __result = null; return; }
                if (__result.count > cellRoom) __result.count = cellRoom;
            }
            if (perTileSpread && __result.count > t.stackCount - perTileSrcCap)
                __result.count = t.stackCount - perTileSrcCap;
            if (__result.count <= 0) { PscFeederHaulContext.Clear(t); __result = null; return; }

            // Intra-unit spread: the per-def target clamps below assume a cross-unit haul that changes the
            // unit total. An over-cap pile spreading within its OWN stockpile doesn't, so the per-tile
            // clamps above are the whole story for this trip.
            if (sourceUnit.IsValid && sourceUnit.Equals(targetUnit)) return;

            var data = PscStorageDataStore.TryGet(canon.Settings);
            var sourceData = sourceUnit.IsValid ? PscStorageDataStore.TryGet(sourceUnit.Settings) : null;
            bool sourceHasBatchEmpty = sourceData != null && sourceData.batchEmpty > 0;
            bool sourceHasLimit = sourceData != null && sourceData.HasEffectiveLimit(t.def);
            // The target-keyed clamps need a limit or fill-batch; the source-keyed batch-empty cancel and
            // the source-over-cap drain clamp below must run even when the target has no PSC policy, so
            // they can't share a single early-out.
            bool targetHasClampPolicy = data != null && (data.HasEffectiveLimit(t.def) || data.batch > 0);
            if (!targetHasClampPolicy && !sourceHasBatchEmpty && !sourceHasLimit) return;

            if (data != null)
            {
                // Upper clamp — cap the planned count so the trip never plans past the maximum. Group-aware:
                // a grouped def clamps against the GROUP's shared cap and the GROUP-SUM room.
                if (data.HasEffectiveLimit(t.def))
                {
                    var lim = data.GetEffectiveLimit(t.def);
                    if (lim.Upper.HasValue)
                    {
                        // Effective room (physical + reserved-inbound), ALWAYS in items via the helper —
                        // a stacks-mode group converts to a member-specific item budget. The current job is
                        // NOT yet registered (that happens after all clamps below), so this excludes its own
                        // reservation -> automatic self-exclusion. No-op when the feature is off.
                        int room = data.GroupAwareItemRoom(t.def, targetUnit, lim.Upper.Value, includeReserved: true);
                        if (room <= 0)
                        {
                            // No room (reservation-overshoot window between admission and job build):
                            // cancel rather than emit a count-0 job, which trips vanilla's
                            // Toils_Haul.ErrorCheckForCarry "Invalid count: 0" Log.Error + 1-item trip.
                            if (PscLog.Enabled) PscLog.Msg(
                                $"limit: cancelled haul of {t.def.defName} -> {targetUnit.UniqueLoadID} (at/over cap, no room)");
                            PscFeederHaulContext.Clear(t);
                            __result = null;
                            return;
                        }
                        if (__result.count > room)
                        {
                            if (PscLog.Enabled) PscLog.Msg(
                                $"limit: clamped haul of {t.def.defName} -> {targetUnit.UniqueLoadID} from {__result.count} to {room} (cap room)");
                            __result.count = room;
                        }
                    }
                }

                // Batch fill (D12): never bring fewer than `batch` in one trip — cancel an under-batch job.
                // Note: when remaining room < batch (a near-full capped unit), the last < batch items are
                // intentionally not hauled ("no small trips"). Opportunistic duplicates are left as vanilla.
                if (data.batch > 0 && __result.count < data.batch)
                {
                    if (PscLog.Enabled) PscLog.Msg(
                        $"limit: cancelled under-batch haul of {t.def.defName} -> {targetUnit.UniqueLoadID} ({__result.count} < batch {data.batch})");
                    PscFeederHaulContext.Clear(t);
                    __result = null;
                }
            }

            // Source-over-cap excess (DESIGN §8 anti-oscillation). When the source unit holds STRICTLY
            // more than its cap for this def, the AllowedToAccept carve-out has made the excess read as
            // misplaced and this trip is draining it. Compute the excess once; reads the cached count, no
            // scan. srcExcess <= 0 means the source is within cap (a normal haul-out), so neither the
            // drain clamp nor the batch-empty exemption below engages.
            // Group-aware via TryGetDrainExcess: per-def gives count-over-cap; a group gives the single
            // deterministic drain member's clamped excess (and 0 for every other member, so only the one
            // chosen member's drain trip is clamped — no N-fold over-drain).
            int srcExcess = 0;
            if (sourceHasLimit) sourceData.TryGetDrainExcess(t.def, sourceUnit, out srcExcess);

            // Drain clamp: clamp an over-cap drain to exactly the excess so a single hauler lands the
            // source on its cap rather than overshooting below it (which could otherwise re-trigger refill
            // and ping-pong). Mirrors the target room clamp above.
            if (__result != null && srcExcess > 0 && __result.count > srcExcess)
            {
                if (PscLog.Enabled) PscLog.Msg(
                    $"limit: clamped over-cap drain of {t.def.defName} from {sourceUnit.UniqueLoadID} to {srcExcess} (excess over cap)");
                __result.count = srcExcess;
            }

            // Batch empty (source-keyed): the trip must take at least batchEmpty OUT of the source — cancel an
            // under-batch removal. Runs last so it sees the final clamped count (vanilla room + carry capacity
            // + the upper clamp above). Best-effort under inventory-haul mods (PUAH / Hauler's Dream) which
            // don't build their bulk jobs through HaulToCellStorageJob; the admission gate is the line there.
            // Exempt an over-cap DRAIN (srcExcess > 0): evacuating excess the player capped out takes
            // priority over "no small removal trips", so a sub-batchEmpty excess is never trapped.
            if (__result != null && sourceHasBatchEmpty && srcExcess <= 0 && __result.count < sourceData.batchEmpty)
            {
                if (PscLog.Enabled) PscLog.Msg(
                    $"batchEmpty: cancelled under-batch removal of {t.def.defName} from {sourceUnit.UniqueLoadID} ({__result.count} < batchEmpty {sourceData.batchEmpty})");
                PscFeederHaulContext.Clear(t);
                __result = null;
            }

            // Reserved-inbound INCREMENT is deliberately NOT done here. This builder
            // (HaulAIUtility.HaulToCellStorageJob) also runs during feasibility probes:
            // WorkGiver_Scanner.HasJobOnThing == JobOnThing(...) != null builds and discards a full job,
            // so reserving here charged a phantom +count on every scan with no matching delivery decrement
            // (the job never starts). That pinned a capped pile's effective count at the cap (all hauling
            // blocked) and desynced HasJobOnThing from the second JobOnThing (the "yielded no actual job"
            // error). The increment now lives at the genuine commitment seam,
            // JobDriver_HaulToCell.Notify_Starting (CountCache_Patches.cs), reached only when a job really
            // starts executing. The effective-room READ above stays — it has no persistent side effect.
        }
    }
}
