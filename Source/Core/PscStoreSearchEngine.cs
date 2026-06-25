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

            // Deep Storage source cell: decline takeover so LWM's own transpiler owns DSU relocation. Phase 2
            // broad stance (documented): PSC cedes ALL DSU-resident items, not only over-capacity ones, rather
            // than reproducing LWM's weight/stack capacity model. Fail-safe false when LWM is absent.
            if (PscReflection.IsItemInDeepStorage(t)) return PscSearchResult.Unaffected;

            // Source over-cap booleans. perTileSpread: the per-tile relocate PREFIX already demoted
            // currentPriority to Unstored (it runs before the engine), so the engine only needs the boolean for
            // the same-unit-relocation gate -- it does NOT re-demote currentPriority for per-tile. plainCell
            // (LWM case A: a non-DSU cell already holding another storable) the prefix does NOT handle, so the
            // engine demotes its own effective current band.
            bool perTileSpread = PscPerTile.TryGetCellCap(t, out int perCellCap) && t.stackCount > perCellCap;
            bool plainCellOverCapacity = SourceCellOverCapacity(t, map);
            bool allowSameUnitRelocation = perTileSpread || plainCellOverCapacity;

            PscSearchContext.TrySource(t, out var source);
            var sourceData = PscSearchContext.SourceData(t);
            StoragePriority effBand = plainCellOverCapacity ? StoragePriority.Unstored : currentPriority;
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

        // Full-key strictly-better test: band dominates (higher wins), then within-band rank (lower wins).
        private static bool StrictlyBetter(StoragePriority candBand, int candRank, StoragePriority curBand, int curRank)
        {
            int cb = (int)candBand, db = (int)curBand;
            if (cb != db) return cb > db;
            return candRank < curRank;
        }

        // LWM case A: the item's current cell already holds ANOTHER storable item (a non-DSU occupied cell),
        // so the item should relocate out. Replicated WHENEVER PSC takes over (not gated on PSC policy) so PSC
        // does not silently regress LWM's relocation of an item sitting on an occupied plain cell. The item
        // itself is exempt. DSU cells are handled by the decline above, so this only ever sees plain cells.
        private static bool SourceCellOverCapacity(Thing t, Map map)
        {
            if (!t.SpawnedOrAnyParentSpawned) return false;
            var list = map.thingGrid.ThingsListAt(t.PositionHeld);
            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i];
                if (o == t) continue;
                if (o.def.EverStorable(false)) return true;
            }
            return false;
        }
    }
}
