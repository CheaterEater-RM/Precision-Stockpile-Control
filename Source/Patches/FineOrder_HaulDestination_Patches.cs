using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace PrecisionStockpileControl
{
    // Fine-order enforcement on the NON-slot-group haul path (design §9; the "container" engine that
    // mirrors the slot-group work in FineOrder_Patches.cs). Graves / sarcophagi are IHaulDestination
    // *containers*, not slot groups: corpse hauling flows through
    // HaulDestinationManager.CompareHaulDestinationPrioritiesDescending (band-only sort) +
    // StoreUtility.TryFindBestBetterNonSlotGroupStorageFor (nearest-wins within a band). Neither is
    // touched by the slot-group fine-order engine, so without these two patches a grave's 1-10 level
    // box would be inert. They make the level box real: within a vanilla band, a better PSC rank beats
    // distance, exactly like the slot-group path.
    //
    // Letters (a-z) are deliberately NOT surfaced on this path yet (the UI hides the letter box here).
    // Note that PscOrder.RankWithinBand already folds letter into rank; that is fine and
    // forward-compatible -- letters simply have no UI on the container path today, so in practice every
    // container carries letter rank 0 and only the sub-tier differentiates. When letter UI lands here it
    // becomes enforced for free.

    // ---- Patch A: same-band comparer tie-break (mirror of HaulDestinationManager_Compare_Patch) ----
    // Postfix on the private HaulDestinationManager.CompareHaulDestinationPrioritiesDescending (patched
    // by string name). When the vanilla band compare tied (__result == 0), break the tie by PSC
    // fine-order so the priority-ordered destination list is stable and rank-descending within a band.
    [HarmonyPatch(typeof(HaulDestinationManager), "CompareHaulDestinationPrioritiesDescending")]
    public static class HaulDestinationManager_CompareDest_Patch
    {
        public static void Postfix(ref int __result, IHaulDestination a, IHaulDestination b)
        {
            if (__result != 0) return;
            if (PscStorageDataStore.IsEmpty) return;
            var sa = a?.GetStoreSettings();
            var sb = b?.GetStoreSettings();
            if (sa == null || sb == null) return;
            // Only a non-default fine-order key can break a same-band tie. IHaulDestination exposes its
            // owning map via IHaulDestination.Map (it extends IStoreSettingsParent). With no fine-order
            // active on this map, CompareWithinBand is always 0, so skip the per-pair rank work in
            // limits/batch/alarm-only colonies. For() is memoised, so the gate is cheap.
            var psc = PscMapComponent.For(a.Map);
            if (psc == null || !psc.anyFineOrderActive) return;
            __result = PscOrder.CompareWithinBand(sa, sb);
            if (__result != 0 && PscLog.Enabled)
            {
                string aId = PscHaulUnit.ResolveSettings(sa).UniqueLoadID;
                string bId = PscHaulUnit.ResolveSettings(sb).UniqueLoadID;
                PscLog.MsgThrottled($"cmpDest:{aId}:{bId}",
                    $"order: same-band dest tiebreak {aId} vs {bId} -> {(__result < 0 ? "a first" : "b first")}");
            }
        }
    }

    // ---- Patch B: rank beats distance in the container store search ----
    // Postfix on StoreUtility.TryFindBestBetterNonSlotGroupStorageFor. Vanilla picks the NEAREST valid
    // container in the best available band; PSC re-picks WITHIN that same band so a better-ranked (but
    // possibly farther) container wins over a nearer worse-ranked one.
    //
    // WHY a postfix re-selection rather than a prefix/transpiler: vanilla's mid-loop decision is a pure
    // distance compare (`num2 > num`), unreachable by a postfix ON the loop and un-expressible by a
    // postfix that only sees the final result. A transpiler was rejected as overkill: the same-band
    // candidate set here is tiny (graves on a map), so a clean band-restricted re-scan in a postfix is
    // cheaper to maintain than a fingerprinted IL edit. This runs ONLY when fine-order is active on the
    // map AND a strictly better-ranked same-band candidate exists; otherwise it exits at the gates.
    //
    // This is tighten-ORDER-only: it never changes __result from true, never selects a different band,
    // and never selects anything the mirrored vanilla checks reject. It can only re-pick among
    // candidates vanilla itself would have accepted in the band it already chose, so it cannot accept
    // what vanilla would not.
    [HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterNonSlotGroupStorageFor),
        new Type[] { typeof(Thing), typeof(Pawn), typeof(Map), typeof(StoragePriority), typeof(Faction), typeof(IHaulDestination), typeof(bool), typeof(bool) },
        new ArgumentType[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal, ArgumentType.Normal })]
    public static class StoreUtility_NonSlotGroup_FineOrder_Patch
    {
        public static void Postfix(ref bool __result, ref IHaulDestination haulDestination, Thing t,
            Pawn carrier, Map map, Faction faction, bool requiresDestReservation)
        {
            // Early-outs in cheapness order.
            if (!__result || haulDestination == null) return;
            if (PscStorageDataStore.IsEmpty) return;
            var psc = PscMapComponent.For(map);
            if (psc == null || !psc.anyFineOrderActive) return;

            var found = haulDestination.GetStoreSettings();
            if (found == null) return;
            StoragePriority band = found.Priority;
            int foundRank = PscOrder.RankWithinBand(found);

            // Same distance origin as vanilla (line 245): the thing's held position, or the carrier's.
            IntVec3 intVec = t.SpawnedOrAnyParentSpawned ? t.PositionHeld : carrier.PositionHeld;

            // Current selection = whatever vanilla chose (the nearest valid in `band`). Any other valid
            // same-band candidate is by definition >= this distance (else vanilla would have picked it),
            // so only a STRICTLY better rank can improve the selection. The (rank, distSq) filter below
            // makes that exact, and naturally skips everything when all same-band ranks are equal.
            IHaulDestination bestDest = haulDestination;
            int bestRank = foundRank;
            float bestDistSq = intVec.DistanceToSquared(haulDestination.Position);
            bool changed = false;

            List<IHaulDestination> list = map.haulDestinationManager.AllHaulDestinationsListInPriorityOrder;
            for (int i = 0; i < list.Count; i++)
            {
                IHaulDestination dest = list[i];
                // Same cheap skips as vanilla (lines 252-255).
                if (dest is ISlotGroupParent || (dest is Building_Grave && !t.CanBeBuried()) || !dest.HaulDestinationEnabled)
                    continue;
                StorageSettings settings = dest.GetStoreSettings();
                if (settings == null) continue;
                int b = (int)settings.Priority;
                // Higher band than the one vanilla settled on: it was genuinely invalid for vanilla (num
                // was still MaxValue when that band was scanned, so a valid higher-band candidate would
                // have won outright). Skip it -- never promote across a band.
                if (b > (int)band) continue;
                // The list is band-descending; past our band there is nothing else to consider.
                if (b < (int)band) break;
                if (ReferenceEquals(dest, haulDestination)) continue;

                int rank = PscOrder.RankWithinBand(settings);
                float distSq = intVec.DistanceToSquared(dest.Position);
                // Consider only candidates STRICTLY better than the current selection under (rank, then
                // distance). Equal-or-worse rank can never win (same-band equals are all farther).
                if (rank > bestRank) continue;
                if (rank == bestRank && distSq >= bestDistSq) continue;

                // Only now pay for the full mirrored validity gate; on a hit it becomes the selection.
                if (!PassesVanillaChecks(dest, t, carrier, map, faction, requiresDestReservation, intVec))
                    continue;
                bestDest = dest;
                bestRank = rank;
                bestDistSq = distSq;
                changed = true;
            }

            if (!changed) return;
            haulDestination = bestDest;
            if (PscLog.Enabled)
            {
                string fromId = PscHaulUnit.ResolveSettings(found).UniqueLoadID;
                string toId = PscHaulUnit.ResolveSettings(bestDest.GetStoreSettings()).UniqueLoadID;
                PscLog.MsgThrottled($"nsg:{fromId}:{toId}",
                    $"order: non-slot-group re-pick {fromId}(rank {foundRank}) -> {toId}(rank {bestRank})");
            }
        }

        // MIRROR of StoreUtility.TryFindBestBetterNonSlotGroupStorageFor validity checks (1.6.4850);
        // re-verify on RimWorld version bumps. Reproduces lines 262-319 EXACTLY (minus the distance
        // compare, which the caller's (rank, distSq) filter already applied): Accepts -> faction match
        // -> forbidden -> reservation (IHaulEnroute / carrier / faction) -> reachability.
        private static bool PassesVanillaChecks(IHaulDestination dest, Thing t, Pawn carrier, Map map,
            Faction faction, bool requiresDestReservation, IntVec3 intVec)
        {
            if (!dest.Accepts(t)) return false;
            Thing thing = dest as Thing;
            if (thing != null && thing.Faction != faction) return false;
            if (thing != null)
            {
                if (carrier != null)
                {
                    if (thing.IsForbidden(carrier)) return false;
                }
                else if (faction != null && thing.IsForbidden(faction))
                {
                    return false;
                }
            }
            if (thing != null && requiresDestReservation)
            {
                if (thing is IHaulEnroute enroute)
                {
                    if (!map.reservationManager.OnlyReservationsForJobDef(thing, JobDefOf.HaulToContainer)
                        || enroute.GetSpaceRemainingWithEnroute(t.def) <= 0)
                        return false;
                }
                else if (carrier != null)
                {
                    if (!carrier.CanReserveNew(thing)) return false;
                }
                else if (faction != null && map.reservationManager.IsReservedByAnyoneOf(thing, faction))
                {
                    return false;
                }
            }
            if (carrier != null)
            {
                if (thing != null)
                {
                    if (!carrier.Map.reachability.CanReach(intVec, thing, PathEndMode.ClosestTouch, TraverseParms.For(carrier)))
                        return false;
                }
                else if (!carrier.Map.reachability.CanReach(intVec, dest.Position, PathEndMode.ClosestTouch, TraverseParms.For(carrier)))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
