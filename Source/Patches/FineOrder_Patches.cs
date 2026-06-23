using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // M4 fine-order seams (design §7, §9):
    //   1. A postfix on HaulDestinationManager.CompareSlotGroupPrioritiesDescending tie-breaks the
    //      sorted group list by PSC fine-order within a vanilla band. This is the conflict-proof
    //      baseline: it makes newly-hauled / unstored items prefer the finer-ranked group, and it
    //      never touches IL.
    //   2. A narrow, version-gated, fail-closed transpiler on StoreUtility.TryFindBestBetterStoreCellFor
    //      lets the search continue past vanilla's same-band break so an already-stored item can be
    //      relocated to a strictly-better same-band group.
    //
    // The two compose with LWM Deep Storage: PSC only changes which GROUPS the search considers; each
    // candidate cell still flows through IsGoodStoreCell / NoStorageBlockersIn (where LWM enforces
    // capacity), so PSC never overfills a Deep Storage cell.

    [HarmonyPatch(typeof(HaulDestinationManager), "CompareSlotGroupPrioritiesDescending")]
    public static class HaulDestinationManager_Compare_Patch
    {
        // Vanilla returns ((int)b.Priority).CompareTo((int)a.Priority) (descending). When two groups
        // share a band (__result == 0), break the tie by fine-order: lower rank (better) sorts first.
        public static void Postfix(ref int __result, SlotGroup a, SlotGroup b)
        {
            if (__result != 0) return;
            if (PscStorageDataStore.IsEmpty) return;
            var sa = a?.Settings;
            var sb = b?.Settings;
            if (sa == null || sb == null) return;
            // Only a non-default fine-order key (sub-tier / letter) can break a same-band tie. With none
            // active on this map, CompareWithinBand is always 0 (every same-band unit ranks equal), so
            // skip the per-pair rank work entirely in limits/batch/alarm-only colonies. Mirrors the gate
            // the re-scan Postfix below already uses. For() is memoised, so the gate itself is cheap.
            var psc = PscMapComponent.For(a.parent?.Map);
            if (psc == null || !psc.anyFineOrderActive) return;
            __result = PscOrder.CompareWithinBand(sa, sb);
            if (__result != 0 && PscLog.Enabled)
            {
                string aId = PscHaulUnit.ResolveSettings(sa).UniqueLoadID;
                string bId = PscHaulUnit.ResolveSettings(sb).UniqueLoadID;
                PscLog.MsgThrottled($"cmp:{aId}:{bId}",
                    $"order: same-band tiebreak {aId} vs {bId} -> {(__result < 0 ? "a first" : "b first")}");
            }
        }
    }

    [HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor))]
    public static class StoreUtility_FineOrder_Patch
    {
        private static readonly System.Reflection.MethodInfo ShouldContinue =
            AccessTools.Method(typeof(PscOrder), nameof(PscOrder.ShouldContinueSearch));

        // Vanilla (1.6.4850):
        //   if ((int)priority < (int)foundPriority) break;          // ldloc priority; ldloc.1; blt BREAK
        //   if ((int)priority <= (int)currentPriority) break;       // ldloc priority; ldarg.3; ble BREAK
        //   ... evaluate this group ...                             // ldloc slotGroup; ...
        //
        // We inject, immediately before the second comparison, a guard that jumps past the break when
        // PscOrder says this same-band group strictly outranks the item's current unit. Mirrors LWM's
        // proven minimal approach (touch only the `ble` half). The `blt` half is left intact so a
        // strictly lower band still terminates the search.
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
            ILGenerator generator)
        {
            var code = new List<CodeInstruction>(instructions);

            int at = -1;
            for (int i = 1; i + 2 < code.Count; i++)
            {
                // Fingerprint: `ldloc <priority>; ldarg.3; ble`, with the next instruction (the
                // if-body) loading the slot group. ldarg.3 + ble is unique to this comparison (the
                // foundPriority init uses ldarg.3 + stloc.1).
                if (code[i].opcode == OpCodes.Ldarg_3
                    && (code[i + 1].opcode == OpCodes.Ble_S || code[i + 1].opcode == OpCodes.Ble)
                    && IsLdloc(code[i - 1])
                    && IsLdloc(code[i + 2]))
                {
                    at = i;
                    break;
                }
            }

            if (at < 0 || ShouldContinue == null)
            {
                Log.Error("[PSC] Fine-order transpiler could not match StoreUtility."
                    + "TryFindBestBetterStoreCellFor; same-band relocation disabled. Newly-hauled "
                    + "ordering (sort tiebreak) is unaffected. (RimWorld version change?)");
                PscOrder.TranspilerFailed = true;
                foreach (var c in code) yield return c;
                yield break;
            }

            var ldPriority = code[at - 1];   // ldloc priority
            var ldSlotGroup = code[at + 2];  // ldloc slotGroup (start of the if-body)

            var continueLabel = generator.DefineLabel();
            ldSlotGroup.labels.Add(continueLabel);

            var injectHead = new CodeInstruction(ldPriority.opcode, ldPriority.operand); // candidatePriority
            // Steal any labels from the original `ldloc priority` onto our injected head so a branch
            // targeting that instruction lands on the guard instead of skipping past it.
            if (ldPriority.labels.Count > 0)
            {
                injectHead.labels.AddRange(ldPriority.labels);
                ldPriority.labels.Clear();
            }

            var inject = new List<CodeInstruction>
            {
                injectHead,                                                   // candidatePriority
                new CodeInstruction(OpCodes.Ldarg_3),                         // currentPriority
                new CodeInstruction(ldSlotGroup.opcode, ldSlotGroup.operand), // candidate slot group
                new CodeInstruction(OpCodes.Ldarg_0),                         // t
                new CodeInstruction(OpCodes.Ldarg_2),                         // map
                new CodeInstruction(OpCodes.Call, ShouldContinue),
                new CodeInstruction(OpCodes.Brtrue, continueLabel),
            };

            // Insert before the `ldloc priority` that feeds the `ble` (after the `blt` half).
            code.InsertRange(at - 1, inject);

            foreach (var c in code) yield return c;
        }

        private static bool IsLdloc(CodeInstruction ci)
        {
            var op = ci.opcode;
            return op == OpCodes.Ldloc_0 || op == OpCodes.Ldloc_1 || op == OpCodes.Ldloc_2
                || op == OpCodes.Ldloc_3 || op == OpCodes.Ldloc_S || op == OpCodes.Ldloc;
        }

        // PscOrder.RankWithinBand minimum: EffectiveSubTier (>= 1) * 100 + LetterRank (>= 0) -> 100.
        // A unit already at this rank is the best possible within its band, so there is nothing to upgrade.
        private const int BestPossibleRank = 100;

        // Rank-primary selection (design §9). The transpiler above only lets the search *consider* a
        // strictly-better same-band group; vanilla's worker still picks the CLOSEST good cell across the
        // whole band (closestDistSquared), so fine-order rank affected iteration order but NOT the winner.
        // An item therefore went to the nearest shelf and then relocated rank-by-rank (5c -> 5b -> 5a -> 5)
        // as the hauler got near each. This postfix upgrades the chosen cell to the CLOSEST placeable cell
        // at the BEST achievable rank within the same band, so full priority (band, then sub-tier, then
        // letter) drives selection and distance only breaks ties among equal-rank groups -- mirroring how
        // vanilla already treats bands, one level finer. Feeder skip-hops rides this: with the deeper chain
        // node made admissible (FeederAllows), the best-rank admissible node (the chain end) wins directly.
        public static void Postfix(Thing t, Pawn carrier, Map map, StoragePriority currentPriority,
            Faction faction, bool needAccurateResult, ref IntVec3 foundCell, bool __result)
        {
            if (!__result) return;                            // vanilla found nothing to upgrade
            if (PscStorageDataStore.IsEmpty) return;          // cheapest gate
            if (t == null) return;
            var psc = PscMapComponent.For(map);
            if (psc == null || !psc.anyFineOrderActive) return;   // no fine-order in use -> vanilla behaviour

            var chosen = PscHaulUnit.ResolveCell(foundCell, map);
            var chosenSettings = chosen.Settings;
            if (!chosen.IsValid || chosenSettings == null) return;
            int chosenRank = PscOrder.RankWithinBand(chosenSettings);
            if (chosenRank <= BestPossibleRank) return;       // already best rank in its band -> nothing better

            // Same basis the vanilla worker uses for distance. Safe: vanilla already evaluated it for
            // __result == true, so this never dereferences a null carrier on the unspawned path.
            if (!t.SpawnedOrAnyParentSpawned && carrier == null) return;
            IntVec3 itemPos = t.SpawnedOrAnyParentSpawned ? t.PositionHeld : carrier.PositionHeld;

            StoragePriority band = chosenSettings.Priority;
            var groups = map.haulDestinationManager.AllGroupsListInPriorityOrder;   // sorted best-first (PSC tiebreak incl.)
            IntVec3 bestCell = IntVec3.Invalid;
            int bestRank = int.MaxValue;
            float bestDist = float.MaxValue;
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                var gs = g.Settings;
                if (gs == null) continue;
                int dband = (int)gs.Priority - (int)band;
                if (dband > 0) continue;                      // better band: vanilla already ruled it unplaceable
                if (dband < 0) break;                         // worse band: nothing left to consider
                int r = PscOrder.RankWithinBand(gs);
                if (r >= chosenRank) break;                   // reached chosen's rank or worse (list is sorted)
                if (bestCell.IsValid && r > bestRank) break;  // best-rank bucket already fully scanned

                // Mirror vanilla's OUTER eligibility guard: TryFindBestBetterStoreCellForIn goes straight to
                // the worker and does NOT check these, so a disabled or wrong-faction storage would otherwise
                // be probed (and wrongly chosen).
                var parent = g.parent;
                if (parent == null || !parent.HaulDestinationEnabled) continue;
                if (parent is Thing pt && pt.Faction != faction) continue;

                var gu = PscHaulUnit.FromSlotGroup(g);
                if (!gu.IsValid || gu.Equals(chosen)) continue;

                if (StoreUtility.TryFindBestBetterStoreCellForIn(t, carrier, map, currentPriority, faction,
                        g, out var cell, needAccurateResult) && cell.IsValid)
                {
                    float d = (itemPos - cell).LengthHorizontalSquared;
                    if (!bestCell.IsValid || r < bestRank || (r == bestRank && d < bestDist))
                    {
                        bestCell = cell;
                        bestRank = r;
                        bestDist = d;
                    }
                }
            }

            if (bestCell.IsValid)
            {
                if (PscLog.Enabled)
                    PscLog.MsgThrottled($"sel:{t.def?.defName}:{chosen.UniqueLoadID}",
                        $"order: {t.def?.defName} selection upgraded rank {chosenRank} -> {bestRank} (best-rank cell)");
                foundCell = bestCell;
            }
        }
    }
}
