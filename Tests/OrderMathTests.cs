using System;
using NUnit.Framework;
using RimWorld;

namespace PrecisionStockpileControl.Tests
{
    [TestFixture]
    public sealed class OrderMathTests
    {
        [Test]
        public void LevelMapping_RoundTripsEveryDisplayedLevel()
        {
            for (int level = 1; level <= 10; level++)
            {
                PscOrder.BandAndSubTierForLevel(level, out StoragePriority band, out byte subTier);

                Assert.That(PscOrder.LevelFor(band, subTier), Is.EqualTo(level), "level " + level);
            }
        }

        [Test]
        public void LevelMapping_StoresBandAnchorsAsUnsetSubTier()
        {
            PscOrder.BandAndSubTierForLevel(1, out StoragePriority critical, out byte criticalTier);
            PscOrder.BandAndSubTierForLevel(10, out StoragePriority low, out byte lowTier);

            Assert.That(critical, Is.EqualTo(StoragePriority.Critical));
            Assert.That(criticalTier, Is.EqualTo((byte)0));
            Assert.That(low, Is.EqualTo(StoragePriority.Low));
            Assert.That(lowTier, Is.EqualTo((byte)0));
        }

        [Test]
        public void ComputeRankWithinBand_CollapsesDisabledDimensions()
        {
            using (new PscSettingsScope(s =>
            {
                s.priorityNumbering = false;
                s.subpriorityLetters = false;
            }))
            {
                Assert.That(PscOrder.ComputeRankWithinBand(2, "z", StoragePriority.Low), Is.EqualTo(100));
                Assert.That(PscOrder.ComputeRankWithinBand(1, null, StoragePriority.Critical), Is.EqualTo(100));
            }
        }

        [Test]
        public void ComputeRankWithinBand_UsesSubTierThenLetterWhenEnabled()
        {
            using (new PscSettingsScope(s =>
            {
                s.priorityNumbering = true;
                s.subpriorityLetters = true;
            }))
            {
                Assert.That(PscOrder.ComputeRankWithinBand(0, null, StoragePriority.Low), Is.EqualTo(200));
                Assert.That(PscOrder.ComputeRankWithinBand(2, "b", StoragePriority.Normal), Is.EqualTo(202));
                Assert.That(PscOrder.ComputeRankWithinBand(1, "a", StoragePriority.Normal), Is.EqualTo(101));
                Assert.That(PscOrder.ComputeRankWithinBand(1, null, StoragePriority.Normal), Is.EqualTo(100));
            }
        }

        [Test]
        public void DisplayLevel_CanReverseLabelsWithoutChangingInternalLevel()
        {
            using (new PscSettingsScope(s => s.reverseOrder = false))
            {
                Assert.That(PscOrder.DisplayLevel(1), Is.EqualTo(1));
                Assert.That(PscOrder.DisplayLevel(10), Is.EqualTo(10));
            }

            using (new PscSettingsScope(s => s.reverseOrder = true))
            {
                Assert.That(PscOrder.DisplayLevel(1), Is.EqualTo(10));
                Assert.That(PscOrder.DisplayLevel(10), Is.EqualTo(1));
            }
        }

        [Test]
        public void CompareKey_HigherBandWinsBeforeWithinBandRank()
        {
            Assert.That(PscOrder.CompareKey(StoragePriority.Critical, 999, StoragePriority.Important, 1),
                Is.LessThan(0));
            Assert.That(PscOrder.CompareKey(StoragePriority.Normal, 100, StoragePriority.Normal, 200),
                Is.LessThan(0));
            Assert.That(PscOrder.CompareKey(StoragePriority.Normal, 300, StoragePriority.Normal, 200),
                Is.GreaterThan(0));
        }

        [Test]
        public void CompareKey_IsStrictTotalOrderForRepresentativeKeys()
        {
            var bands = new[]
            {
                StoragePriority.Unstored,
                StoragePriority.Low,
                StoragePriority.Normal,
                StoragePriority.Preferred,
                StoragePriority.Important,
                StoragePriority.Critical,
            };
            var ranks = new[] { 100, 101, 126, 200, 226 };

            foreach (var aBand in bands)
            foreach (int aRank in ranks)
            foreach (var bBand in bands)
            foreach (int bRank in ranks)
            {
                int ab = Math.Sign(PscOrder.CompareKey(aBand, aRank, bBand, bRank));
                int ba = Math.Sign(PscOrder.CompareKey(bBand, bRank, aBand, aRank));
                Assert.That(ab, Is.EqualTo(-ba), $"antisymmetry {aBand}/{aRank} vs {bBand}/{bRank}");
            }

            foreach (var aBand in bands)
            foreach (int aRank in ranks)
            foreach (var bBand in bands)
            foreach (int bRank in ranks)
            foreach (var cBand in bands)
            foreach (int cRank in ranks)
            {
                bool aBeforeOrEqualB = PscOrder.CompareKey(aBand, aRank, bBand, bRank) <= 0;
                bool bBeforeOrEqualC = PscOrder.CompareKey(bBand, bRank, cBand, cRank) <= 0;
                if (!aBeforeOrEqualB || !bBeforeOrEqualC) continue;

                Assert.That(PscOrder.CompareKey(aBand, aRank, cBand, cRank), Is.LessThanOrEqualTo(0),
                    $"transitivity {aBand}/{aRank}, {bBand}/{bRank}, {cBand}/{cRank}");
            }
        }
    }
}
