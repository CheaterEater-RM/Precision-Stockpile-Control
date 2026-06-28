using System.Reflection;
using NUnit.Framework;
using RimWorld;

namespace PrecisionStockpileControl.Tests
{
    // Behavioral coverage of the real PscAdmissionIndex.HardReject decision tree (the tighten-only
    // crown jewel), complementing the structural checks in AdmissionSourceGuardTests. HardReject is
    // internal, so it is invoked by reflection. The unit/source are passed as default(PscHaulUnit):
    // default.Map is null, so the feeder branch (PscMapComponent.For(unit.Map)) early-outs to "no
    // reject", and the cached count path never dereferences the unit — so the cap / over-cap-drain /
    // hysteresis / batch branches are exercised headless with no live Map. target is a bare
    // StorageSettings reference used only as the PscStorageDataStore key.
    [TestFixture]
    public sealed class AdmissionDecisionTests
    {
        private static readonly MethodInfo HardRejectMethod = typeof(PscAdmissionIndex).GetMethod(
            "HardReject", BindingFlags.Static | BindingFlags.NonPublic);

        [TearDown]
        public void ClearStore() => PscStorageDataStore.Clear();

        [Test]
        public void HaulIn_PerDefCap_RejectsAtAndAboveUpperButNotBelow()
        {
            var def = TestDefs.Thing("PSC_AdmSteel", stackLimit: 75);
            var h = new StorageDataHarness();
            h.SetPerDefLimit(def, upper: 100);          // no lower => hysteresis gate inactive
            var target = TestDefs.StorageKey();
            PscStorageDataStore.Set(target, h.Data);

            h.SetCount(def, items: 99, physicalStacks: 2);
            Assert.That(HardReject(target, TestDefs.ItemOf(def, 1), sourceIsTarget: false, out _), Is.False,
                "below cap must admit");

            h.SetCount(def, items: 100, physicalStacks: 2);
            Assert.That(HardReject(target, TestDefs.ItemOf(def, 1), sourceIsTarget: false, out string reason), Is.True,
                "exactly at cap must reject (n >= upper)");
            Assert.That(reason, Is.EqualTo("overCap"));
        }

        [Test]
        public void OwnContents_DrainOnlyTriggersStrictlyAboveUpper()
        {
            var def = TestDefs.Thing("PSC_AdmWood", stackLimit: 75);
            var h = new StorageDataHarness();
            h.SetPerDefLimit(def, upper: 100);
            var target = TestDefs.StorageKey();
            PscStorageDataStore.Set(target, h.Data);

            h.SetCount(def, items: 100, physicalStacks: 2);  // exactly at cap: own contents stay valid (D16)
            Assert.That(HardReject(target, TestDefs.ItemOf(def, 1), sourceIsTarget: true, out _), Is.False);

            h.SetCount(def, items: 101, physicalStacks: 2);  // strictly over: misplaced so vanilla drains it
            Assert.That(HardReject(target, TestDefs.ItemOf(def, 1), sourceIsTarget: true, out string reason), Is.True);
            Assert.That(reason, Is.EqualTo("overCapDrain"));
        }

        [Test]
        public void HaulIn_Hysteresis_BlocksInsideDeadbandUntilRefilling()
        {
            var def = TestDefs.Thing("PSC_AdmMeat", stackLimit: 75);
            var h = new StorageDataHarness();
            h.SetPerDefLimit(def, upper: 100, lower: 10);
            var target = TestDefs.StorageKey();
            PscStorageDataStore.Set(target, h.Data);
            h.SetCount(def, items: 50, physicalStacks: 1);   // in the deadband, below upper

            Assert.That(HardReject(target, TestDefs.ItemOf(def, 1), sourceIsTarget: false, out string blocked), Is.True,
                "not refilling in the deadband must block intake");
            Assert.That(blocked, Is.EqualTo("hysteresis"));

            h.Data.GetLimit(def).refill = PscRefillState.Refilling;
            Assert.That(HardReject(target, TestDefs.ItemOf(def, 1), sourceIsTarget: false, out _), Is.False,
                "refilling state must admit intake");
        }

        [Test]
        public void HaulIn_BatchFill_RejectsSourceStackSmallerThanBatch()
        {
            var def = TestDefs.Thing("PSC_AdmCloth", stackLimit: 75);
            var h = new StorageDataHarness();
            h.Data.batch = 10;                                // batch fill, no per-def limit
            h.SetCount(def, items: 0, physicalStacks: 0);
            var target = TestDefs.StorageKey();
            PscStorageDataStore.Set(target, h.Data);

            Assert.That(HardReject(target, TestDefs.ItemOf(def, 5), sourceIsTarget: false, out string reason), Is.True,
                "a source stack below the batch size cannot start a trip");
            Assert.That(reason, Is.EqualTo("underBatch"));

            Assert.That(HardReject(target, TestDefs.ItemOf(def, 10), sourceIsTarget: false, out _), Is.False,
                "a full-batch source stack into an uncapped unit is admitted");
        }

        private static bool HardReject(StorageSettings target, Verse.Thing t, bool sourceIsTarget, out string reason)
        {
            // (target, t, unit, source, sourceData, sourceIsTarget, planning, out reason)
            var args = new object[]
            {
                target, t, default(PscHaulUnit), default(PscHaulUnit), null, sourceIsTarget, false, null,
            };
            bool result = (bool)HardRejectMethod.Invoke(null, args);
            reason = (string)args[7];
            return result;
        }
    }
}
