using NUnit.Framework;

namespace PrecisionStockpileControl.Tests
{
    [TestFixture]
    public sealed class GroupLimitMathTests
    {
        [Test]
        public void GroupAwareItemRoom_UngroupedUsesPhysicalOrEffectiveCount()
        {
            using (new PscSettingsScope(s => s.reservedFillCounting = true))
            {
                var def = TestDefs.Thing("PSC_TestSteel", stackLimit: 75);
                var h = new StorageDataHarness();
                h.SetPerDefLimit(def, upper: 40);
                h.SetCount(def, items: 20, physicalStacks: 1);
                h.Data.AddReservedInbound(def, 5);

                Assert.That(h.Data.GroupAwareItemRoom(def, default, 40, includeReserved: false), Is.EqualTo(20));
                Assert.That(h.Data.GroupAwareItemRoom(def, default, 40, includeReserved: true), Is.EqualTo(15));
            }
        }

        [Test]
        public void GroupAwareItemRoom_ItemsGroupUsesSharedTotal()
        {
            using (new PscSettingsScope(s => s.reservedFillCounting = true))
            {
                var a = TestDefs.Thing("PSC_TestMeatA", stackLimit: 75);
                var b = TestDefs.Thing("PSC_TestMeatB", stackLimit: 75);
                var h = new StorageDataHarness();
                h.AddGroup(PscGroupCountMode.Items, upper: 30, lower: null, a, b);
                h.SetCount(a, items: 10, physicalStacks: 1);
                h.SetCount(b, items: 5, physicalStacks: 1);
                h.Data.AddReservedInbound(a, 4);

                Assert.That(h.Data.GroupAwareItemRoom(a, default, 30, includeReserved: false), Is.EqualTo(15));
                Assert.That(h.Data.GroupAwareItemRoom(a, default, 30, includeReserved: true), Is.EqualTo(11));
            }
        }

        [Test]
        public void GroupAwareItemRoom_StacksGroupConvertsFreeCellsAndPartialSlackToItems()
        {
            using (new PscSettingsScope())
            {
                var meat = TestDefs.Thing("PSC_TestCellsMeat", stackLimit: 75);
                var leather = TestDefs.Thing("PSC_TestCellsLeather", stackLimit: 75);
                var h = new StorageDataHarness();
                h.AddGroup(PscGroupCountMode.Stacks, upper: 3, lower: null, meat, leather);

                h.SetCount(meat, items: 30, physicalStacks: 1);
                h.SetCount(leather, items: 75, physicalStacks: 1);
                Assert.That(h.Data.GroupAwareItemRoom(meat, default, 3, includeReserved: true), Is.EqualTo(120));

                h.SetCount(meat, items: 100, physicalStacks: 2);
                h.SetCount(leather, items: 75, physicalStacks: 1);
                Assert.That(h.Data.GroupAwareItemRoom(meat, default, 3, includeReserved: true), Is.EqualTo(50));

                h.SetCount(meat, items: 100, physicalStacks: 2);
                h.SetCount(leather, items: 150, physicalStacks: 2);
                Assert.That(h.Data.GroupAwareItemRoom(meat, default, 3, includeReserved: true), Is.EqualTo(0));
            }
        }

        [Test]
        public void TryGetDrainExcess_PerDefDrainsOnlyStrictlyAboveUpper()
        {
            var def = TestDefs.Thing("PSC_TestWood", stackLimit: 75);
            var h = new StorageDataHarness();
            h.SetPerDefLimit(def, upper: 100);

            h.SetCount(def, items: 100, physicalStacks: 2);
            Assert.That(h.Data.TryGetDrainExcess(def, default, out int atCap), Is.False);
            Assert.That(atCap, Is.EqualTo(0));

            h.SetCount(def, items: 126, physicalStacks: 2);
            Assert.That(h.Data.TryGetDrainExcess(def, default, out int overCap), Is.True);
            Assert.That(overCap, Is.EqualTo(26));
        }

        [Test]
        public void TryGetDrainExcess_ItemsGroupChoosesLargestMemberAndClampsToGroupOverage()
        {
            var a = TestDefs.Thing("PSC_TestLargestA", stackLimit: 75, shortHash: 10);
            var b = TestDefs.Thing("PSC_TestLargestB", stackLimit: 75, shortHash: 2);
            var h = new StorageDataHarness();
            h.AddGroup(PscGroupCountMode.Items, upper: 100, lower: null, a, b);
            h.SetCount(a, items: 80, physicalStacks: 2);
            h.SetCount(b, items: 50, physicalStacks: 1);

            Assert.That(h.Data.TryGetDrainExcess(a, default, out int excessA), Is.True);
            Assert.That(excessA, Is.EqualTo(30));
            Assert.That(h.Data.TryGetDrainExcess(b, default, out int excessB), Is.False);
            Assert.That(excessB, Is.EqualTo(0));
        }

        [Test]
        public void TryGetDrainExcess_ItemsGroupTieBreaksByShortHash()
        {
            var highHash = TestDefs.Thing("PSC_TestTieHigh", stackLimit: 75, shortHash: 100);
            var lowHash = TestDefs.Thing("PSC_TestTieLow", stackLimit: 75, shortHash: 3);
            var h = new StorageDataHarness();
            h.AddGroup(PscGroupCountMode.Items, upper: 90, lower: null, highHash, lowHash);
            h.SetCount(highHash, items: 50, physicalStacks: 1);
            h.SetCount(lowHash, items: 50, physicalStacks: 1);

            Assert.That(h.Data.TryGetDrainExcess(highHash, default, out _), Is.False);
            Assert.That(h.Data.TryGetDrainExcess(lowHash, default, out int excess), Is.True);
            Assert.That(excess, Is.EqualTo(10));
        }

        [Test]
        public void TryGetDrainExcess_StacksGroupDrainsMostCellsByCeilAverage()
        {
            var manyCells = TestDefs.Thing("PSC_TestManyCells", stackLimit: 75, shortHash: 10);
            var fewCells = TestDefs.Thing("PSC_TestFewCells", stackLimit: 75, shortHash: 1);
            var h = new StorageDataHarness();
            h.AddGroup(PscGroupCountMode.Stacks, upper: 3, lower: null, manyCells, fewCells);
            h.SetCount(manyCells, items: 31, physicalStacks: 3);
            h.SetCount(fewCells, items: 150, physicalStacks: 2);

            Assert.That(h.Data.TryGetDrainExcess(manyCells, default, out int excess), Is.True);
            Assert.That(excess, Is.EqualTo(22));
            Assert.That(h.Data.TryGetDrainExcess(fewCells, default, out _), Is.False);
        }
    }
}
