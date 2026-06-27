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

        // Reused per search to copy a linked StorageGroup's cells out of the SHARED static temp
        // StorageGroup.tmpCellsList before the inner IsGoodStoreCell loop. StorageGroup.CellsList clears and
        // refills that one static list on every read, so holding the reference across calls that may re-enter
        // a StorageGroup.CellsList read (a modded IsGoodStoreCell / NoStorageBlockersIn postfix, a nested
        // search) would clobber it mid-scan (AGENTS.md "never retain StorageGroup.CellsList"). Plain SlotGroups
        // have stable per-parent CellsLists and skip this. ThreadStatic for the same defensive isolation as
        // seenCanon; cleared by ClearThreadStaticState.
        [ThreadStatic] private static List<IntVec3> cellBuffer;

        public static PscSearchResult TryFindBestStoreCell(Thing t, Pawn carrier, Map map,
            StoragePriority currentPriority, Faction faction, in PscSearchOptions options, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;

            // Phase 1 (1D): main-thread tripwire for a genuine concurrent entry DURING GAMEPLAY -- a threading
            // mod or the RimWorld Multiplayer sim thread hitting PSC's non-concurrency-safe count/policy model
            // (PscStorageData.counts / reservedInbound). Gated on ProgramState.Playing: save LOADING and map
            // GENERATION run on the LongEventHandler ASYNC (background) thread with ProgramState == MapInitializing
            // (Game.LoadGame / MapGenerator.GenerateMap), so a store search a load triggers (e.g. the haulables
            // lister re-checking already-stored items via IsInValidBestStorage) is legitimately off the Unity main
            // thread -- but it is the SOLE worker (the main thread only renders the loading screen), so there is no
            // concurrent mutation and it must NOT warn. Dev-gated (PscLog.Enabled short-circuits), so normal play
            // and profiling pay one bool read.
            if (PscLog.Enabled && Current.ProgramState == ProgramState.Playing && !UnityData.IsInMainThread)
                Log.ErrorOnce("[PSC] store-search engine entered off the main thread during play; PSC's count "
                    + "model is not thread-safe (threading mod / MP sim thread?).", 0x1C5A0101);

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

            // Carried-item feeder restore (PUAH): remember the hauling pawn so TryFeederReject can look up the
            // captured feeder source for an item that has no live source (in this pawn's inventory). Cleared by
            // the Finalizer with the rest of the per-search context.
            PscSearchContext.SetCarrier(carrier);

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

            // The engine NEVER demotes the item's band on its own: candidates are compared against the
            // currentPriority vanilla passed (already Unstored when the per-tile prefix demoted a genuine floor
            // over-cap). A correctly stored item therefore only relocates to a STRICTLY better unit, never down
            // its (uphill-priority) feeder chain. Vanilla/LWM enforce cell capacity at placement and DSU cells
            // are ceded above, so there is no real non-DSU over-cap case left to handle here.
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
                // Cross-unit: must strictly out-rank the item's current full key (band, then within-band rank).
                if (!sourceIsTarget && PscOrder.CompareKey(settings.Priority, rank, currentPriority, currentRank) >= 0)
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

                // Modded multi-stack storage (Adaptive Storage Framework / Reel's Expanded Storage and the like)
                // enforces BUILDING-level capacity. Vanilla's worker stopped calling parent.Accepts in 1.5 (it
                // calls Settings.AllowedToAccept instead), so ASF re-adds a capacity gate via a
                // TryFindBestBetterStoreCellForWorker PREFIX -- which this integrated scan bypasses, because it
                // never calls the worker. Honor the capacity directly and mod-agnostically: ISlotGroupParent :
                // IHaulDestination, and a non-vanilla Building_Storage subtype re-maps Accepts onto a capacity
                // check. Verified basis: ASF's Building_Storage-subtype Accepts override DOES include its
                // capacity check (so parent.Accepts captures it even though we skip ASF's worker prefix), and
                // LWM Deep Storage's per-cell capacity is honored separately through the StoreUtility.IsGoodStoreCell
                // path the cell scan below already calls. Vanilla shelves (exactly Building_Storage) and stockpile
                // zones have filter-only Accepts == the AllowedToAccept already confirmed above, so they skip this
                // entirely and the vanilla hot path pays only one type test. KNOWN LIMITATION (DESIGN compat
                // stance): a framework that enforces capacity ONLY in a worker prefix and leaves Accepts
                // filter-only would be missed here (PSC could over-admit); no such shipped framework is known.
                // Standalone units ONLY: a linked StorageGroup's per-building capacity can't be judged from a
                // single member without risking a false reject of another member's free cells, and admission is
                // tighten-only (never wrongly reject a legal target), so linked groups fall through to vanilla's
                // per-cell IsGoodStoreCell checks below.
                if (!(canon is StorageGroup) && parent is Building_Storage moddedStore
                    && moddedStore.GetType() != typeof(Building_Storage))
                {
                    bool capacityOk;
                    PscEngineScope.BypassAdmissionBackstop = true;   // base.Accepts -> AllowedToAccept: don't re-enter PSC admission
                    try { capacityOk = parent.Accepts(t); }
                    finally { PscEngineScope.BypassAdmissionBackstop = false; }
                    if (!capacityOk) continue;
                }

                // Hard admission (planning: effective count + per-search memo). sourceIsTarget routes the
                // own-contents / over-cap-drain branch for an intra-unit relocation candidate. Skipped (Phase 3b
                // §3/§5) when needAdmission proved no PSC rule can reject this item -- a no-op omission.
                if (!skipAdmission && PscAdmissionIndex.HardReject(unit.Settings, t, unit, source, sourceData,
                        sourceIsTarget, planning: true, out _)) continue;

                // Vanilla-faithful per-group cell scan (mirror TryFindBestBetterStoreCellForWorker EXACTLY):
                // shared bestDist across groups, Rand.Range only when accurate and only after AllowedToAccept
                // passed (matching the worker's early return before num), the per-group i >= num early break.
                // CellsList handling: a linked StorageGroup's CellsList is the SHARED static temp
                // StorageGroup.tmpCellsList (cleared + refilled on every read), so holding it across the inner
                // IsGoodStoreCell loop is unsafe -- a reachable StorageGroup.CellsList read (a modded
                // IsGoodStoreCell / NoStorageBlockersIn postfix, a nested search) would clobber it mid-scan
                // (AGENTS.md "never retain StorageGroup.CellsList"). So copy a StorageGroup's cells into a reused
                // per-search buffer first; a plain SlotGroup's CellsList is a stable per-parent list (shelf
                // cachedOccupiedCells / zone cells), iterated directly so vanilla shelves/zones pay no copy.
                // ExcludedCells (PUAH skipCells, the Phase 4 bulk adapters): cells the bulk caller has already
                // allocated to. Checked INLINE in this scan -- the only place that needs it, since the PUAH
                // extra-item adapter replaces PUAH's own body -- rather than via a global IsGoodStoreCell postfix
                // that would pay a dispatch on every cell scan game-wide. null / empty on the ordinary vanilla
                // path, so that path pays one local read and no Contains.
                List<IntVec3> cells;
                if (canon is StorageGroup)
                {
                    var buf = cellBuffer ?? (cellBuffer = new List<IntVec3>(128));
                    buf.Clear();
                    buf.AddRange(canon.CellsList);   // copy out of the shared static temp before any IsGoodStoreCell call
                    cells = buf;
                }
                else
                {
                    cells = canon.CellsList;          // stable per-parent list (shelf / zone) -- no copy needed
                }
                int count = cells.Count;
                int num = options.NeedAccurateResult ? Mathf.FloorToInt(count * Rand.Range(0.005f, 0.018f)) : 0;
                var excluded = options.ExcludedCells;
                for (int i = 0; i < count; i++)
                {
                    IntVec3 c = cells[i];
                    if (excluded != null && excluded.Contains(c)) continue;
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

            if (bestCell.IsValid)
            {
                PscEngineScope.IntendedUnitGroup = chosenGroup;     // read by the HD re-validation postfix
                cell = bestCell;
                if (PscLog.Enabled)
                {
                    // Name the chosen unit and the item's source so the trace shows WHERE stock lands and
                    // FROM where -- "chosen <overflow>" vs "chosen <chain node>" is the whole diagnosis, and the
                    // bare band/rank could not distinguish them.
                    string chosenId = chosenGroup != null ? PscHaulUnit.FromSlotGroup(chosenGroup).UniqueLoadID : "?";
                    string srcHint = source.IsValid ? source.UniqueLoadID : "no-source(carried/loose)";
                    PscLog.MsgThrottled($"eng:{t.def?.defName}",
                        $"engine: {t.def?.defName} chosen {chosenId} (band {bestBand} rank {bestRank}) [from {srcHint}]");
                }
                return PscSearchResult.Found;
            }
            return PscSearchResult.NoLegalTarget;
        }

        // Release the per-search thread-static buffers at end of search (called from
        // StoreUtility_Engine_Patch.Finalizer, alongside the scope + PscSearchContext clears). They are
        // otherwise only cleared at the START of the next search, so between searches they would retain the
        // candidates' ISlotGroup/StorageGroup refs (seenCanon) and a copied cell list (cellBuffer) and could
        // pin a removed temporary map until the next search on this thread.
        public static void ClearThreadStaticState()
        {
            seenCanon?.Clear();
            cellBuffer?.Clear();
        }

        // Full per-search teardown: the engine scopes + the per-search context + the thread-static buffers.
        // The vanilla-prefix path runs this via StoreUtility_Engine_Patch.Finalizer; a DIRECT engine caller (the
        // Phase 4 PUAH extra-item adapter calls TryFindBestStoreCell outside that Harmony method, so its
        // Finalizer never fires) must call this itself in a finally, or VanillaFallbackPlanning / the carrier
        // memo / the candidate buffers would leak into the next search on this thread. Idempotent.
        public static void ResetSearchState()
        {
            PscEngineScope.BypassAdmissionBackstop = false;
            PscEngineScope.IntendedUnitGroup = null;
            PscEngineScope.VanillaFallbackPlanning = false;
            ClearThreadStaticState();
            PscSearchContext.Clear();
        }
    }
}
