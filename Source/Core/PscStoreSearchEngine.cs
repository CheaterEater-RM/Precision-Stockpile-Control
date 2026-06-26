using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // The owned slot-group selection engine (store-search rewrite, Phase 2 + 3a). It replaces PSC's per-group
    // admission fan-out (the AllowedToAccept planning postfix + the fine-order transpiler + the rank-primary
    // re-scan) with ONE integrated pass over the already full-key-sorted AllGroupsListInPriorityOrder: it walks
    // the groups best-first, applies PSC admission (PscAdmissionIndex.HardReject) plus the live vanilla filter
    // (AllowedToAccept, backstop bypassed) per canonical unit, and scans each unit's cells with vanilla's own
    // closest-good-cell rule sharing ONE distance accumulator across the best-key bucket (mirrors
    // TryFindBestBetterStoreCellForWorker). The retained AllowedToAccept backstop is bypassed only around the
    // per-unit filter confirm, so admission is paid exactly once.
    //
    // Tri-state: Unaffected (run vanilla), NoLegalTarget (PSC applies, no admissible+legal cell), Found.
    //
    // Verified vanilla facts this relies on (1.6.4850):
    //   - The list is kept full-key sorted: vanilla's InsertionSort(CompareSlotGroupPrioritiesDescending) plus
    //     PSC's HaulDestinationManager_Compare_Patch fine-order tiebreak, re-sorted on every priority / fine-order
    //     edit. So one best-first walk needs no per-search copy or sort.
    //   - The worker applies NO currentPriority break and shares ONE closestDistSquared across a band
    //     (StoreUtility.cs:173-205); the inline scan reproduces that exactly (shared bestDist). That is why a
    //     within-bucket cell pick can differ from the deleted Phase 2 per-candidate …ForIn (which used a fresh
    //     accumulator per group) and instead matches vanilla. See the "shared-accumulator refinement" note in
    //     STORE_SEARCH_REWRITE_PHASE3A_PLAN.md.
    public static class PscStoreSearchEngine
    {
        public enum PscSearchResult { Unaffected, NoLegalTarget, Found }

        // Reused per search to dedup a linked StorageGroup's member slot groups to one canonical scan (a linked
        // StorageGroup lists many member slot groups -> one canonical unit -> one scan over all its cells).
        // ThreadStatic: defensive per-thread isolation if ever reached off-main by a threading caller (vanilla
        // 1.6 runs it main-thread; see PscSearchContext / PHASE4 §6.1). Reference equality is correct here
        // (ISlotGroup impls do not override Equals, per PscHaulUnit). Cleared at the top of each search and by
        // ClearThreadStaticState (the engine prefix Finalizer) so it never pins a removed map's group objects.
        [ThreadStatic] private static HashSet<ISlotGroup> seenCanon;

        public static PscSearchResult TryFindBestStoreCell(Thing t, Pawn carrier, Map map,
            StoragePriority currentPriority, Faction faction, in PscSearchOptions options, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;

            // Phase 1 (1D): main-thread tripwire. Vanilla's haul store-search is synchronous and main-thread
            // (PHASE4 §6.1); PSC's live count/policy model (PscStorageData.counts / reservedInbound) is NOT
            // concurrency-safe, so the residual ThreadStatic scopes would not actually protect an off-main
            // caller (a threading mod) — they would silently corrupt counts. Catch that loudly instead of
            // pretending to be safe. Dev-gated (PscLog.Enabled short-circuits the thread check), so normal
            // play and profiling pay only one bool read.
            if (PscLog.Enabled && !UnityData.IsInMainThread)
                Log.ErrorOnce("[PSC] store-search engine entered off the main thread; PSC's count model is not "
                    + "thread-safe (threading mod?). See STORE_SEARCH_REWRITE_PHASE4_DESIGN.md §6.1.", 0x1C5A0101);

            // Conservative, map-LOCAL opt-in gate (broaden-first): no PSC policy anywhere, then none on THIS
            // map (IsEmpty is global). A policy-free map stays byte-for-byte vanilla even when another map is
            // managed. Per-item narrowing behind the anyXxxActive flags is a later optimisation.
            if (PscStorageDataStore.IsEmpty) return PscSearchResult.Unaffected;
            if (t == null || map == null) return PscSearchResult.Unaffected;
            var psc = PscMapComponent.For(map);
            if (psc == null || !psc.anyPscActive) return PscSearchResult.Unaffected;

            // Deep Storage source cell: decline takeover so LWM's own transpiler owns DSU relocation. PSC cedes
            // ALL DSU-resident items rather than reproducing LWM's weight/stack capacity model. Fail-safe false
            // when LWM is absent. PSC policy is still active though, so the AllowedToAccept backstop becomes the
            // planning gate for this ceded search: mark it planning so the backstop reads effective/reserved
            // counts (else concurrent DSU relocations overshoot a capped destination). Cleared by the Finalizer.
            if (PscReflection.IsItemInDeepStorage(t))
            {
                PscEngineScope.VanillaFallbackPlanning = true;
                return PscSearchResult.Unaffected;
            }

            // perTileSpread: the per-tile relocate PREFIX already demoted currentPriority to Unstored (it runs
            // before the engine), so the engine only needs the boolean for the same-unit-relocation gate -- it
            // does NOT re-demote currentPriority for per-tile. Same-unit relocation is allowed ONLY for per-tile
            // spread: the downstream HaulToCellStorageJob same-unit cancel exempts ONLY perTileSpread
            // (Admission_Patches.cs), so a non-per-tile same-unit pick would be cancelled and strand the item.
            bool perTileSpread = PscPerTile.TryGetCellCap(t, out int perCellCap) && t.stackCount > perCellCap;
            bool allowSameUnitRelocation = perTileSpread;

            PscSearchContext.TrySource(t, out var source);
            var sourceData = PscSearchContext.SourceData(t);

            // Source-only rejections (Phase 3b §4.2): two source rules make EVERY cross-unit target illegal, so
            // decide once and skip the whole group walk instead of rejecting each candidate. Both are
            // target-independent, so this is behavior-neutral — the per-candidate gates stay in HardReject /
            // TryFeederReject for the AllowedToAccept backstop (planning:false) callers, which never run this
            // preflight. Return NoLegalTarget (not Unaffected): the item cannot legally leave its source, so
            // vanilla must NOT be let loose to find a target for it. The cheap field checks gate the (rare)
            // AllowedToAccept call, which preserves the evacuate-disallowed-contents exemption (a leftover the
            // source no longer accepts stays movable). Per-tile is exempt (an intra-unit spread is not the item
            // leaving its unit); the no-route onlyToDestinations case mirrors the per-candidate Opt-B
            // short-circuit (HasAnyDestination == false => no candidate can ever present a functional edge).
            if (!perTileSpread && source.IsValid && sourceData != null
                && ((sourceData.onlyToDestinations && !psc.Links.HasAnyDestination(source.UniqueLoadID))
                    || (sourceData.batchEmpty > 0 && t.stackCount < sourceData.batchEmpty))
                && source.Settings.AllowedToAccept(t))
            {
                return PscSearchResult.NoLegalTarget;
            }

            // Per-item admission narrowing (Phase 3b §3/§5). Skip the per-candidate HardReject when NO PSC rule
            // could reject this item -- every term is PSC-policy-keyed (rebuilt synchronously on the
            // NotifyPolicyChanged seam), so this is EXACT, not bounded-staleness. Behavior-neutral: skipAdmission
            // is taken only when HardReject would return false for every candidate, so omitting it is a no-op vs
            // the current engine. No vanilla cede -- the engine still runs, so the AllowedToAccept backstop stays
            // bypassed around the per-unit confirm and linked-storage canonicalization is unchanged (see the
            // PHASE3B post-Codex revision). Per-tile keeps admission: its over-cap drain / intra-unit relocation
            // needs HardReject(sourceIsTarget). needAdmission must over-approximate every HardReject reject path
            // (the Codex-confirmed coverage table); over-inclusion only costs an unnecessary HardReject.
            bool needAdmission =
                   psc.HasRestrictedDef(t.def)                                  // a cap / hysteresis / over-cap-drain could fire
                || psc.anyBatchActive                                           // a destination-batch gate could fire
                || psc.anyIntakeBlockActive                                     // an Off/RetrieveOnly target blocks intake
                || (sourceData != null && sourceData.batchEmpty > 0)            // source batchEmpty (live)
                || (psc.anyFeederActive && (!source.IsValid                     // loose/carried under active feeder
                        || (sourceData != null && sourceData.onlyToDestinations)
                        || psc.anyOnlyFromSourceActive));                       // a target could onlyFromSource-reject
            bool skipAdmission = !needAdmission && !perTileSpread;

            // The engine NEVER demotes the item's band on its own: effBand is exactly the currentPriority vanilla
            // passed (already Unstored when the per-tile prefix demoted a genuine floor over-cap). A correctly
            // stored item therefore only relocates to a STRICTLY better unit, never down its (uphill-priority)
            // feeder chain. Vanilla/LWM enforce cell capacity at placement and DSU cells are ceded above, so
            // there is no real non-DSU over-cap case left to handle here.
            StoragePriority effBand = currentPriority;
            int currentRank = source.IsValid ? PscOrder.RankWithinBand(source.Settings) : 0;

            IntVec3 itemPos = t.SpawnedOrAnyParentSpawned ? t.PositionHeld
                : (carrier != null ? carrier.PositionHeld : IntVec3.Invalid);

            var seen = seenCanon ?? (seenCanon = new HashSet<ISlotGroup>());
            seen.Clear();

            IntVec3 bestCell = IntVec3.Invalid;
            int bestBand = int.MinValue, bestRank = int.MaxValue;
            float bestDist = float.MaxValue;
            ISlotGroup chosenGroup = null;

            // One pass over the already full-key-sorted list (vanilla's band InsertionSort + PSC's Compare patch
            // keep it ordered; PSC re-sorts on every priority / fine-order edit). No copy, no re-sort, no O(n^2)
            // dedup, no per-candidate …ForIn.
            var groups = map.haulDestinationManager.AllGroupsListInPriorityOrder;
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                var settings = g.Settings;
                if (settings == null) continue;
                int cb = (int)settings.Priority;
                int rank = PscOrder.RankWithinBand(settings);

                // Best-key bucket fully scanned: every later group is worse-or-equal (sorted list), so stop.
                if (bestCell.IsValid && (cb < bestBand || (cb == bestBand && rank > bestRank))) break;

                // Vanilla outer-loop eligibility (faction + HaulDestinationEnabled) that the worker does not apply.
                var parent = g.parent;
                if (parent == null || !parent.HaulDestinationEnabled) continue;
                if (parent is Thing pt && pt.Faction != faction) continue;

                // Canonicalize FIRST, dedup SECOND, scan THIRD: a linked StorageGroup's member slot groups
                // collapse to one canonical unit scanned once over its full CellsList.
                var unit = PscHaulUnit.FromSlotGroup(g);
                if (!unit.IsValid) continue;
                var canon = unit.group;
                if (!seen.Add(canon)) continue;

                bool sourceIsTarget = source.IsValid && source.Equals(unit);
                // Same-unit: only a deliberate intra-unit relocation (per-tile spread) may target the item's own
                // unit; a normal item never relocates within its unit.
                if (sourceIsTarget && !allowSameUnitRelocation) continue;
                // Cross-unit: must strictly out-rank the item's EFFECTIVE current full key.
                if (!sourceIsTarget && !StrictlyBetter(settings.Priority, rank, effBand, currentRank))
                {
                    // Phase 3b §4.4: the list is full-key sorted (band desc, then within-band rank), so once a
                    // cross-unit group is no longer strictly better than the item's current key, no later group
                    // can be either — stop the walk instead of scanning the rest only to reject each. Same
                    // sortedness the bucket break above already relies on. Behavior-neutral: every skipped group
                    // would have failed this same gate and continued. Per-tile is exempt: its own unit
                    // (sourceIsTarget) can appear later in the list and must be reached, and the per-tile prefix
                    // demoted its currentPriority to Unstored so this gate rarely fires for it anyway — keep the
                    // old continue to preserve that path exactly.
                    if (!allowSameUnitRelocation) break;
                    continue;
                }

                // Live vanilla filter FIRST (Phase 3b §4.1). PSC admission is tighten-only, so if vanilla
                // (or another mod's AllowedToAccept postfix) already rejects this target, skip the heavier
                // PSC feeder/cap/batch work entirely. Behavior-neutral vs running HardReject first: both
                // gates are pure (HardReject mutates nothing while planning) and AND-ed, so the order never
                // changes which groups pass — it only avoids the admission cost on filter-rejected candidates.
                // The backstop is bypassed ONLY around this confirm; HardReject below still runs with it
                // un-bypassed (unchanged), and its nested source AllowedToAccept self-early-outs on
                // sourceIsTarget regardless of order. Mirrors the worker's single AllowedToAccept(t) gate,
                // which it runs BEFORE the sampling below.
                bool ok;
                PscEngineScope.BypassAdmissionBackstop = true;
                try { ok = canon.Settings.AllowedToAccept(t); }
                finally { PscEngineScope.BypassAdmissionBackstop = false; }
                if (!ok) continue;

                // Hard admission (planning: effective count + per-search memo). sourceIsTarget routes the
                // own-contents / over-cap-drain branch for an intra-unit relocation candidate. Skipped (Phase 3b
                // §3/§5) when needAdmission proved no PSC rule can reject this item -- a no-op omission.
                if (!skipAdmission && PscAdmissionIndex.HardReject(unit.Settings, t, unit, source, sourceData,
                        sourceIsTarget, planning: true, out _)) continue;

                // Vanilla-faithful per-group cell scan (mirror TryFindBestBetterStoreCellForWorker EXACTLY):
                // shared bestDist across groups, Rand.Range only when accurate and only after AllowedToAccept
                // passed (matching the worker's early return before num), the per-group i >= num early break.
                // CellsList is a static temp: iterated now, NEVER retained. ExcludedCells (PUAH skipCells, the
                // Phase 4 bulk adapters) is held across the scan so the Phase 4 IsGoodStoreCell postfix sees it;
                // the engine clears it in finally, so the option is honored without relying on the Harmony
                // Finalizer (which also clears it, defensively). null / empty on the vanilla path.
                var cells = canon.CellsList;
                int count = cells.Count;
                int num = options.NeedAccurateResult ? Mathf.FloorToInt(count * Rand.Range(0.005f, 0.018f)) : 0;
                PscEngineScope.ExcludedCells = options.ExcludedCells;
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        IntVec3 c = cells[i];
                        float d = itemPos.IsValid ? (itemPos - c).LengthHorizontalSquared : 0f;
                        if (!(d > bestDist) && StoreUtility.IsGoodStoreCell(c, map, t, carrier, faction))
                        {
                            bestCell = c;
                            bestDist = d;
                            bestBand = cb;
                            bestRank = rank;
                            chosenGroup = canon;
                            if (i >= num) break;
                        }
                    }
                }
                finally { PscEngineScope.ExcludedCells = null; }
            }

            if (bestCell.IsValid)
            {
                PscEngineScope.IntendedUnitGroup = chosenGroup;     // read by the HD re-validation postfix
                cell = bestCell;
                if (PscLog.Enabled)
                    PscLog.MsgThrottled($"eng:{t.def?.defName}",
                        $"engine: {t.def?.defName} chosen band {bestBand} rank {bestRank}");
                return PscSearchResult.Found;
            }
            return PscSearchResult.NoLegalTarget;
        }

        // Release the per-search thread-static dedup set at end of search (called from
        // StoreUtility_Engine_Patch.Finalizer, alongside the scope + PscSearchContext clears). It is otherwise
        // only cleared at the START of the next search, so between searches it would retain the candidates'
        // ISlotGroup/StorageGroup refs and could pin a removed temporary map until the next search on this thread.
        public static void ClearThreadStaticState() => seenCanon?.Clear();

        // Full-key strictly-better test: band dominates (higher wins), then within-band rank (lower wins).
        private static bool StrictlyBetter(StoragePriority candBand, int candRank, StoragePriority curBand, int curRank)
        {
            int cb = (int)candBand, db = (int)curBand;
            if (cb != db) return cb > db;
            return candRank < curRank;
        }
    }
}
