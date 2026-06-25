using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // The owned slot-group selection engine (store-search rewrite, Phase 2). It replaces PSC's per-group
    // admission fan-out (the AllowedToAccept planning postfix + the fine-order transpiler + the rank-primary
    // re-scan): it ranks ALL eligible groups by the full (band, within-band rank) key, hard-admits each via
    // the shared PscAdmissionIndex.HardReject, and delegates the legal-cell pick to vanilla
    // TryFindBestBetterStoreCellForIn. The retained AllowedToAccept backstop is bypassed only around that
    // delegated probe (PscEngineScope.BypassAdmissionBackstop), so admission is paid exactly once.
    //
    // Tri-state: Unaffected (run vanilla), NoLegalTarget (PSC applies, no admissible+legal cell), Found.
    //
    // Verified vanilla fact this relies on: the worker (TryFindBestBetterStoreCellForWorker) applies NO
    // currentPriority break -- it runs slotGroup.Settings.AllowedToAccept(t) then takes the closest good cell.
    // So the currentPriority handed to …ForIn is moot; the engine owns priority filtering (the StrictlyBetter
    // gate), and …ForIn just supplies the closest legal cell in a candidate unit.
    public static class PscStoreSearchEngine
    {
        public enum PscSearchResult { Unaffected, NoLegalTarget, Found }

        private struct Candidate
        {
            public ISlotGroup group;     // canonical (StorageGroup for a linked unit) -> one …ForIn covers all cells
            public PscHaulUnit unit;
            public StoragePriority band;
            public int rank;             // PscOrder.RankWithinBand (lower = better)
            public int order;            // gather index: a deterministic tiebreak for equal keys (MP-safe)
        }

        // Best-first: band descending, then within-band rank ascending, then gather order (so equal-key units
        // keep AllGroupsListInPriorityOrder order -- deterministic across multiplayer clients). Cached so Sort
        // does not allocate a delegate per search.
        private static readonly Comparison<Candidate> ByKey = (a, b) =>
        {
            int c = ((int)b.band).CompareTo((int)a.band);
            if (c != 0) return c;
            c = a.rank.CompareTo(b.rank);
            if (c != 0) return c;
            return a.order.CompareTo(b.order);
        };

        // Reused per search (the search may run on off-main reachability threads, so keep it thread-local).
        [ThreadStatic] private static List<Candidate> candidateBuffer;

        public static PscSearchResult TryFindBestStoreCell(Thing t, Pawn carrier, Map map,
            StoragePriority currentPriority, Faction faction, in PscSearchOptions options, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;

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
            // The engine NEVER demotes the item's band on its own: effBand is exactly the currentPriority vanilla
            // passed (already Unstored when the per-tile prefix demoted a genuine floor over-cap). A correctly
            // stored item therefore only relocates to a STRICTLY better unit, never down its (uphill-priority)
            // feeder chain. An earlier build demoted to Unstored whenever the item shared a cell with another
            // storable (a "LWM case A" probe), but vanilla shelves hold 3 stacks per cell, so it fired on every
            // normal shelf item and hauled correctly placed goods back to lower chain nodes. Vanilla/LWM enforce
            // cell capacity at placement and DSU cells are ceded above, so there is no real non-DSU over-cap
            // case left to handle here.
            StoragePriority effBand = currentPriority;
            int currentRank = source.IsValid ? PscOrder.RankWithinBand(source.Settings) : 0;

            // Gather eligible candidate units, deduped to canonical units (a linked StorageGroup lists many
            // member slot groups -> one canonical unit -> one …ForIn over all its cells). Mirrors the vanilla
            // outer-loop eligibility guards (faction, HaulDestinationEnabled) that …ForIn itself does not apply.
            var candidates = candidateBuffer ?? (candidateBuffer = new List<Candidate>());
            candidates.Clear();
            var groups = map.haulDestinationManager.AllGroupsListInPriorityOrder;
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                var settings = g.Settings;
                if (settings == null) continue;
                var parent = g.parent;
                if (parent == null || !parent.HaulDestinationEnabled) continue;
                if (parent is Thing pt && pt.Faction != faction) continue;
                var unit = PscHaulUnit.FromSlotGroup(g);
                if (!unit.IsValid) continue;
                bool dup = false;
                for (int j = 0; j < candidates.Count; j++)
                    if (candidates[j].unit.Equals(unit)) { dup = true; break; }
                if (dup) continue;
                candidates.Add(new Candidate
                {
                    group = unit.group,
                    unit = unit,
                    band = settings.Priority,
                    rank = PscOrder.RankWithinBand(settings),
                    order = candidates.Count,
                });
            }
            candidates.Sort(ByKey);

            IntVec3 itemPos = t.SpawnedOrAnyParentSpawned ? t.PositionHeld
                : (carrier != null ? carrier.PositionHeld : IntVec3.Invalid);

            IntVec3 bestCell = IntVec3.Invalid;
            int bestBand = int.MinValue, bestRank = int.MaxValue;
            float bestDist = float.MaxValue;
            ISlotGroup chosenGroup = null;

            for (int i = 0; i < candidates.Count; i++)
            {
                var cnd = candidates[i];
                int cb = (int)cnd.band;
                bool sourceIsTarget = source.IsValid && source.Equals(cnd.unit);

                // Same-unit: only a deliberate intra-unit relocation (per-tile spread or plain-cell over-cap)
                // may target the item's own unit; a normal item never relocates within its unit.
                if (sourceIsTarget && !allowSameUnitRelocation) continue;
                // Cross-unit: must strictly out-rank the item's EFFECTIVE current full key.
                if (!sourceIsTarget && !StrictlyBetter(cnd.band, cnd.rank, effBand, currentRank)) continue;
                // Best-key bucket fully scanned: every later candidate is worse-or-equal (sorted), so stop.
                if (bestCell.IsValid && (cb < bestBand || (cb == bestBand && cnd.rank > bestRank))) break;
                // Hard admission (planning: effective count + per-search memo). sourceIsTarget routes the
                // own-contents / over-cap-drain branch for an intra-unit relocation candidate.
                if (PscAdmissionIndex.HardReject(cnd.unit.Settings, t, cnd.unit, source, sourceData,
                        sourceIsTarget, planning: true, out _)) continue;

                // Delegate the legal-cell pick to vanilla, bracketed by the admission bypass + excluded cells
                // (the ONLY place the bypass flag is set). The worker's AllowedToAccept(t) runs as the live
                // filter confirm at zero PSC cost; ExcludedCells is null on the vanilla path (adapters only).
                IntVec3 c;
                bool found;
                PscEngineScope.BypassAdmissionBackstop = true;
                PscEngineScope.ExcludedCells = options.ExcludedCells;
                try
                {
                    found = StoreUtility.TryFindBestBetterStoreCellForIn(t, carrier, map, currentPriority,
                        faction, cnd.group, out c, options.NeedAccurateResult);
                }
                finally
                {
                    PscEngineScope.BypassAdmissionBackstop = false;
                    PscEngineScope.ExcludedCells = null;
                }

                if (found && c.IsValid)
                {
                    float d = itemPos.IsValid ? (itemPos - c).LengthHorizontalSquared : 0f;
                    if (!bestCell.IsValid || cb > bestBand || (cb == bestBand && cnd.rank < bestRank)
                        || (cb == bestBand && cnd.rank == bestRank && d < bestDist))
                    {
                        bestCell = c;
                        bestBand = cb;
                        bestRank = cnd.rank;
                        bestDist = d;
                        chosenGroup = cnd.group;
                    }
                }
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

        // Release the per-search thread-static buffer at end of search (called from
        // StoreUtility_Engine_Patch.Finalizer, alongside the scope + PscSearchContext clears). The buffer is
        // otherwise only cleared at the START of the next search, so between searches it would retain the
        // candidates' ISlotGroup/StorageGroup refs and could pin a removed temporary map until the next search
        // on this thread. Clear() zeroes the backing array (net48), dropping those refs.
        public static void ClearThreadStaticState() => candidateBuffer?.Clear();

        // Full-key strictly-better test: band dominates (higher wins), then within-band rank (lower wins).
        private static bool StrictlyBetter(StoragePriority candBand, int candRank, StoragePriority curBand, int curRank)
        {
            int cb = (int)candBand, db = (int)curBand;
            if (cb != db) return cb > db;
            return candRank < curRank;
        }
    }
}
