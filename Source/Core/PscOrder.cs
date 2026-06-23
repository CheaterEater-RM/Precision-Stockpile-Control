using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // PSC's fine-order layer (design §9). Vanilla offers 5 priority bands and proximity-only
    // tiebreak inside a band. PSC adds two PSC-owned half-steps:
    //   - sub-tier  (1..2 per band; 0 = unset = the band's anchor) -> the optional 1-10 numbering
    //   - letter    (a..z; empty = highest) -> the always-available a-z subpriority
    //
    // The vanilla StoragePriority enum is NEVER changed (D6). The fine-order key collapses cleanly:
    // when 1-10 numbering is off, sub-tier is ignored (pairs merge back to their band) and only
    // letters refine ordering; with the mod removed, both fields are inert and units read as their
    // plain band.
    //
    // Ordering is expressed as a "rank within band" where LOWER = higher priority (better). A unit
    // at sub-tier 1 / no letter is the best within its band.
    public static class PscOrder
    {
        // Set true if the fail-closed transpiler could not match the vanilla IL (version drift or a
        // conflicting IL edit). Same-band relocation is then off; the sort tiebreak still works.
        public static bool TranspilerFailed;

        // The default sub-tier a band collapses to (its 1-10 anchor): Low anchors at tier 2 (level
        // 10), every other band at tier 1 (levels 1/3/5/7). See design §9 anchor table.
        public static int AnchorTier(StoragePriority band) => band == StoragePriority.Low ? 2 : 1;

        // Effective sub-tier used for comparison/display. Unset (0) resolves to the band anchor.
        // When 1-10 numbering is off, every unit collapses to tier 1 so sub-tier stops differentiating.
        private static int EffectiveSubTier(byte subTier, StoragePriority band)
        {
            if (PscMod.Settings == null || !PscMod.Settings.priorityNumbering) return 1;
            if (subTier == 1 || subTier == 2) return subTier;
            return AnchorTier(band);
        }

        // a..z -> 1..26; empty/null -> 0 (sorts highest within tier).
        private static int LetterRank(string letter)
        {
            if (string.IsNullOrEmpty(letter)) return 0;
            char c = char.ToLowerInvariant(letter[0]);
            if (c < 'a' || c > 'z') return 0;
            return c - 'a' + 1;
        }

        // Bumped whenever PscMod.Settings.priorityNumbering changes (settings apply / reset). The
        // PscStorageData rank cache stamps the generation it computed under and recomputes on a mismatch,
        // so a numbering toggle transparently invalidates every cached rank without enumerating units.
        public static int NumberingGeneration;

        // Pure "rank within band" (lower = better): effective sub-tier (dominant) then letter. No
        // per-call lookups — the cached form (PscStorageData.GetRank) and the no-data fast path below
        // both call this. Depends on the global priorityNumbering via EffectiveSubTier, so any caller
        // that memoises the result must also key on NumberingGeneration.
        public static int ComputeRankWithinBand(byte subTier, string letter, StoragePriority band)
            => EffectiveSubTier(subTier, band) * 100 + LetterRank(letter);

        public static int RankWithinBand(StorageSettings settings)
        {
            if (settings == null) return AnchorTier(StoragePriority.Normal) * 100;
            var data = PscStorageDataStore.TryGet(settings);
            // Most units have no PSC data — compute inline (subTier 0 / no letter), never touch a cache.
            if (data == null) return ComputeRankWithinBand(0, null, settings.Priority);
            return data.GetRank(settings.Priority);
        }

        // Within-band tiebreak for the sort comparator: negative => a sorts before b (higher
        // priority first). Both settings are assumed to share a band (the postfix only calls this
        // when the vanilla band comparison tied).
        public static int CompareWithinBand(StorageSettings a, StorageSettings b)
        {
            return RankWithinBand(a).CompareTo(RankWithinBand(b));
        }

        // Full priority comparison (band, then fine-order). Negative => a is strictly higher
        // priority than b. Used by the feeder validity rule (D5 unified onto the fine-order key).
        public static int Compare(StorageSettings a, StorageSettings b)
        {
            int bandA = (int)(a?.Priority ?? StoragePriority.Unstored);
            int bandB = (int)(b?.Priority ?? StoragePriority.Unstored);
            if (bandA != bandB) return bandB.CompareTo(bandA); // higher band first
            return RankWithinBand(a).CompareTo(RankWithinBand(b));
        }

        // dest strictly outranks source by the full key.
        public static bool Outranks(StorageSettings higher, StorageSettings lower)
            => Compare(higher, lower) < 0;

        // ---- Auto-priority on feeder links (D4) ----
        // Outcome of an auto-priority attempt: nothing was needed (Skipped), a letter step was applied
        // (Placed), the anchor was already at the letter extreme (Clamped), or the two units are in
        // different priority BANDS so no within-band letter step can order them (CrossBand). Auto-priority
        // never changes a unit's band on its own (that would silently demote/promote a band the player set
        // — N2); CrossBand means "tell the player to set the band by hand", nothing was changed.
        public enum AutoOrderResult { Skipped, Placed, Clamped, CrossBand }

        // Step toward lower priority: none -> a -> b -> ... -> z. Clamps at z (clamped=true when the
        // input is already z). Empty/junk-below-'a' is treated as no-letter, so the first step is 'a'.
        private static string StepLetterDown(string letter, out bool clamped)
        {
            clamped = false;
            if (string.IsNullOrEmpty(letter)) return "a";
            char c = char.ToLowerInvariant(letter[0]);
            if (c < 'a') return "a";
            if (c >= 'z') { clamped = true; return "z"; }
            return ((char)(c + 1)).ToString();
        }

        // Step toward higher priority: z -> ... -> b -> a -> none. Clamps at no-letter (clamped=true
        // when the input is already empty). 'a' (or junk below) steps up to no-letter.
        private static string StepLetterUp(string letter, out bool clamped)
        {
            clamped = false;
            if (string.IsNullOrEmpty(letter)) { clamped = true; return null; }
            char c = char.ToLowerInvariant(letter[0]);
            if (c <= 'a') return null;
            if (c > 'z') return "z";
            return ((char)(c - 1)).ToString();
        }

        // Write a unit's full fine-order key (band + sub-tier + letter) and notify, mirroring the
        // manual letter/level boxes (PscPriorityBox). The vanilla enum stays authoritative (D6).
        private static void ApplyOrder(StorageSettings settings, StoragePriority band, byte subTier, string letter)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            if (data == null) return;
            settings.Priority = band;
            data.subTier = subTier;
            data.letter = string.IsNullOrEmpty(letter) ? null : letter;
            PscLog.Msg($"order: auto-set band {band} subTier {subTier} letter {(data.letter ?? "(none)")}");
            PscMapComponent.NotifyOrderChanged(settings);
        }

        // ---- Sequential subpriority painter helpers ----
        // The painter (Designator_PscSubpriorityPaint) seeds from one unit's order and walks letters
        // downward onto each painted unit. These thin wrappers share the EXACT stepping/write the
        // auto-priority path uses (StepLetterDown / ApplyOrder), so the painter stays a single source
        // of truth with PlaceSourceBelowDest rather than re-deriving the a-z math.

        // Next lower letter (none->a->...->z). `clamped` is true only when the INPUT was already z.
        public static string StepPaintLetter(string letter, out bool clamped)
            => StepLetterDown(letter, out clamped);

        // True when `letter` is already the bottom of the sequence (z) — the painter refuses to step
        // past it.
        public static bool LetterIsZ(string letter)
            => !string.IsNullOrEmpty(letter) && char.ToLowerInvariant(letter[0]) == 'z';

        // Write a unit's full fine-order key (band + sub-tier + letter) and notify, identical to the
        // auto-priority path (the painter adopts the seed's band/sub-tier on every painted unit).
        public static void ApplyPaintStep(StorageSettings settings, StoragePriority band, byte subTier, string letter)
            => ApplyOrder(settings, band, subTier, letter);

        // Place `source` one fine-order letter-step BELOW `dest` (adopting the dest's band + sub-tier).
        // No-op when the dest already strictly outranks the source (Skipped); makes no change and
        // reports Clamped when the dest is already at the bottom letter z (no strictly-lower slot).
        public static AutoOrderResult PlaceSourceBelowDest(StorageSettings dest, StorageSettings source)
        {
            if (dest == null || source == null) return AutoOrderResult.Skipped;
            if (Outranks(dest, source)) return AutoOrderResult.Skipped;
            // Past the Outranks check, dest does NOT outrank source. If they are in different bands that can
            // only be because source's band is HIGHER; a within-band letter step can't fix it, and adopting
            // dest's band would silently DEMOTE source (N2). Refuse and let the player set the band by hand.
            if (dest.Priority != source.Priority) return AutoOrderResult.CrossBand;
            var destData = PscStorageDataStore.TryGet(dest);
            string newLetter = StepLetterDown(destData?.letter, out bool clamped);
            if (clamped) return AutoOrderResult.Clamped;
            ApplyOrder(source, dest.Priority, destData?.subTier ?? 0, newLetter);
            return AutoOrderResult.Placed;
        }

        // Place `dest` one fine-order letter-step ABOVE `source` (adopting the source's band + sub-tier).
        // No-op when the dest already strictly outranks the source (Skipped); makes no change and
        // reports Clamped when the source is already at the top (no letter; no strictly-higher slot).
        public static AutoOrderResult PlaceDestAboveSource(StorageSettings source, StorageSettings dest)
        {
            if (source == null || dest == null) return AutoOrderResult.Skipped;
            if (Outranks(dest, source)) return AutoOrderResult.Skipped;
            // Different bands here means dest's band is LOWER than source's; adopting source's band would
            // silently PROMOTE dest (N2). Refuse so auto-priority never changes a band on its own.
            if (source.Priority != dest.Priority) return AutoOrderResult.CrossBand;
            var srcData = PscStorageDataStore.TryGet(source);
            string newLetter = StepLetterUp(srcData?.letter, out bool clamped);
            if (clamped) return AutoOrderResult.Clamped;
            ApplyOrder(dest, source.Priority, srcData?.subTier ?? 0, newLetter);
            return AutoOrderResult.Placed;
        }

        // ---- Transpiler helper (StoreUtility.TryFindBestBetterStoreCellFor) ----
        // Returns true when the search should CONTINUE past vanilla's "priority <= currentPriority"
        // break: i.e. this candidate group shares the item's current band but strictly outranks the
        // item's current unit by fine-order. When fine-order is inactive this always returns false,
        // so vanilla's break runs unchanged (byte-identical behavior).
        //
        // Correctness depends on the sort postfix ordering same-band groups by fine-order, so the
        // strictly-better groups are visited before the break point is reached.
        public static bool ShouldContinueSearch(StoragePriority candidatePriority,
            StoragePriority currentPriority, SlotGroup candidate, Thing t, Map map)
        {
            if (PscStorageDataStore.IsEmpty) return false;
            // Only same-band continuation; a strictly lower band must still break (vanilla).
            if ((int)candidatePriority != (int)currentPriority) return false;

            var psc = PscMapComponent.For(map);
            if (psc == null || !psc.anyFineOrderActive) return false;

            var candidateUnit = PscHaulUnit.FromSlotGroup(candidate);
            if (!candidateUnit.IsValid) return false;

            // The item's current (source) unit + rank are invariant across all candidates in one search;
            // PscSearchContext resolves them once (shared with the admission postfix) and the planning-
            // scope Finalizer clears it. Loose / unspawned / carried => no current unit => nothing to upgrade.
            if (!PscSearchContext.TrySource(t, out var currentUnit)) return false;
            // Never relocate within the same unit (or a linked sibling sharing one StorageGroup).
            if (candidateUnit.Equals(currentUnit)) return false;

            int candidateRank = RankWithinBand(candidateUnit.Settings);
            int currentRank = PscSearchContext.SourceRank(t);
            bool continueSearch = candidateRank < currentRank; // candidate strictly better
            if (continueSearch && PscLog.Enabled)
                PscLog.MsgThrottled($"scs:{candidateUnit.UniqueLoadID}:{currentUnit.UniqueLoadID}",
                    $"order: relocate {t?.def?.defName}? candidate {candidateUnit.UniqueLoadID} rank {candidateRank} < current {currentUnit.UniqueLoadID} rank {currentRank} -> continue search");
            return continueSearch;
        }

        // ---- 1-10 numbering mapping (design §9) ----
        // Band base levels: Critical 1, Important 3, Preferred 5, Normal 7, Low 9 (+ sub-tier-1).
        public static int LevelFor(StoragePriority band, byte subTier)
        {
            int baseLevel;
            switch (band)
            {
                case StoragePriority.Critical: baseLevel = 1; break;
                case StoragePriority.Important: baseLevel = 3; break;
                case StoragePriority.Preferred: baseLevel = 5; break;
                case StoragePriority.Normal: baseLevel = 7; break;
                default: baseLevel = 9; break; // Low
            }
            int tier = (subTier == 1 || subTier == 2) ? subTier : AnchorTier(band);
            return baseLevel + (tier - 1);
        }

        public static void BandAndSubTierForLevel(int level, out StoragePriority band, out byte subTier)
        {
            if (level < 1) level = 1; else if (level > 10) level = 10;
            if (level <= 2) band = StoragePriority.Critical;
            else if (level <= 4) band = StoragePriority.Important;
            else if (level <= 6) band = StoragePriority.Preferred;
            else if (level <= 8) band = StoragePriority.Normal;
            else band = StoragePriority.Low;
            int tier = ((level - 1) % 2) + 1;
            // Store the band anchor as 0 (unset) so picking a default level keeps the unit out of the
            // fine-order-active set.
            subTier = tier == AnchorTier(band) ? (byte)0 : (byte)tier;
        }

        // Display number, with the optional reverse-order label flip (1 <-> 10). Internal ordering
        // is unaffected (design §11.5).
        public static int DisplayLevel(int level)
            => (PscMod.Settings != null && PscMod.Settings.reverseOrder) ? (11 - level) : level;
    }
}
