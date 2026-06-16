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
    }
}
