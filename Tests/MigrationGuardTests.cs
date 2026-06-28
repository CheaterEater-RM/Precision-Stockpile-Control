using NUnit.Framework;
using UnityEngine;

namespace PrecisionStockpileControl.Tests
{
    [TestFixture]
    public sealed class MigrationGuardTests
    {
        [Test]
        public void MigrationStillRecognizesSupportedForeignRideAlongNodes()
        {
            string src = RepoPaths.Read("Source", "Core", "PscMigration.cs");

            foreach (string token in new[]
            {
                "\"stackGap\"",
                "\"hysteresis\"",
                "\"limitSettings\"",
                "\"allowedPerItem\"",
                "\"keys\"",
                "\"values\"",
                "\"stackGapPercents\"",
                "\"fillPercent\"",
                "\"duplicatesLimit\"",
                "\"cellfillPercentage\"",
            })
                Assert.That(src, Does.Contain(token), "Missing migration token " + token);
        }

        [Test]
        public void MigrationKeepsSoftDependencyMarkerTypes()
        {
            string src = RepoPaths.Read("Source", "Core", "PscMigration.cs");

            Assert.That(src, Does.Contain("\"SatisfiedStorage.Hysteresis\""));
            Assert.That(src, Does.Contain("\"VarietyMattersStockpile.StorageLimits\""));
            Assert.That(src, Does.Contain("\"StorageUpperBound.StackGapData\""));
            Assert.That(src, Does.Contain("AccessTools.TypeByName(TypeSatisfiedStorage) == null"));
            Assert.That(src, Does.Contain("AccessTools.TypeByName(TypeVarietyMatters) == null"));
            Assert.That(src, Does.Contain("AccessTools.TypeByName(TypeStackGap) == null"));
        }

        [Test]
        public void StackGapPercentFormulaMatchesDocumentedApproximation()
        {
            Assert.That(StackGapPercentToItems(basis: 75, slots: 4, percent: 0.5f), Is.EqualTo(36));
            Assert.That(StackGapPercentToItems(basis: 75, slots: 4, percent: 0.25f), Is.EqualTo(4));
            Assert.That(StackGapPercentToItems(basis: 75, slots: 4, percent: 0.01f), Is.EqualTo(4));
        }

        [Test]
        public void SatisfiedAndVarietyMatterPercentFormulasMatchDocumentedApproximation()
        {
            Assert.That(SatisfiedLower(capacity: 300, fillPercent: 25f), Is.EqualTo(75));
            Assert.That(SatisfiedLower(capacity: 300, fillPercent: 33.3f), Is.EqualTo(99));
            Assert.That(VarietyMattersUpper(duplicatesLimit: 3, maxStackBasis: 75), Is.EqualTo(225));
            Assert.That(VarietyMattersLower(capacity: 300, cellFill: 0.5f, upper: 120), Is.EqualTo(120));
        }

        [Test]
        public void MigrationSourceUsesFormulaPiecesAndExpandsDefaultsToAllowedDefs()
        {
            string src = RepoPaths.Read("Source", "Core", "PscMigration.cs");

            Assert.That(src, Does.Contain("Mathf.Pow(rec.gapMax, StackGapLogFactor)"));
            Assert.That(src, Does.Contain("Mathf.Pow(rec.gapMin, StackGapLogFactor)"));
            Assert.That(src, Does.Contain("Mathf.FloorToInt(capacity * (rec.fillPercent / 100f))"));
            Assert.That(src, Does.Contain("rec.dupLimit * MaxStackBasis(settings)"));
            Assert.That(src, Does.Contain("Mathf.FloorToInt(capacity * rec.cellFill)"));
            Assert.That(src, Does.Contain("foreach (var def in settings.filter.AllowedThingDefs)"));
            Assert.That(src, Does.Contain("if (upper.HasValue && !lim.Upper.HasValue)"));
            Assert.That(src, Does.Contain("if (lower.HasValue && !lim.Lower.HasValue)"));
        }

        private static int StackGapPercentToItems(int basis, int slots, float percent)
        {
            int perStack = Mathf.Max(1, Mathf.RoundToInt(basis * Mathf.Pow(percent, 3f)));
            return Mathf.Clamp(perStack * Mathf.Max(1, slots), 1, 1000000);
        }

        private static int SatisfiedLower(int capacity, float fillPercent)
            => Mathf.Clamp(Mathf.FloorToInt(capacity * (fillPercent / 100f)), 0, 1000000);

        private static int VarietyMattersUpper(int duplicatesLimit, int maxStackBasis)
            => Mathf.Clamp(duplicatesLimit * maxStackBasis, 1, 1000000);

        private static int VarietyMattersLower(int capacity, float cellFill, int? upper)
        {
            int lower = Mathf.Clamp(Mathf.FloorToInt(capacity * cellFill), 0, 1000000);
            if (upper.HasValue && lower > upper.Value) lower = upper.Value;
            return lower;
        }
    }
}
