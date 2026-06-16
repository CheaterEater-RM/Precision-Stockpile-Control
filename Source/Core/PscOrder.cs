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

        // Lower = better. Composite of effective sub-tier (dominant) then letter.
        private static int RankWithinBand(PscStorageData data, StoragePriority band)
        {
            byte sub = data?.subTier ?? 0;
            string letter = data?.letter;
            return EffectiveSubTier(sub, band) * 100 + LetterRank(letter);
        }

        public static int RankWithinBand(StorageSettings settings)
        {
            if (settings == null) return AnchorTier(StoragePriority.Normal) * 100;
            return RankWithinBand(PscStorageDataStore.TryGet(settings), settings.Priority);
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

            var currentUnit = PscHaulUnit.ResolveCurrent(t);
            if (!currentUnit.IsValid) return false;
            // Never relocate within the same unit (or a linked sibling sharing one StorageGroup).
            if (candidateUnit.Equals(currentUnit)) return false;

            int candidateRank = RankWithinBand(candidateUnit.Settings);
            int currentRank = RankWithinBand(currentUnit.Settings);
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
