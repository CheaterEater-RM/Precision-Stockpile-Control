using NUnit.Framework;

namespace PrecisionStockpileControl.Tests
{
    [TestFixture]
    public sealed class ReservationCountingTests
    {
        [Test]
        public void ReservedInboundKeepsEffectiveCountAtOrAbovePhysical()
        {
            using (new PscSettingsScope(s => s.reservedFillCounting = true))
            {
                var def = TestDefs.Thing("PSC_TestReserved", stackLimit: 75);
                var h = new StorageDataHarness();
                h.SetCount(def, items: 12, physicalStacks: 1);

                Assert.That(h.Data.HasAnyReserved(), Is.False);
                Assert.That(h.Data.GetEffectiveCount(def, default), Is.EqualTo(12));

                h.Data.AddReservedInbound(def, 7);
                Assert.That(h.Data.HasAnyReserved(), Is.True);
                Assert.That(h.Data.GetReservedInbound(def), Is.EqualTo(7));
                Assert.That(h.Data.GetEffectiveCount(def, default), Is.EqualTo(19));

                h.Data.AddReservedInbound(def, -2);
                Assert.That(h.Data.GetReservedInbound(def), Is.EqualTo(5));
                Assert.That(h.Data.GetEffectiveCount(def, default), Is.EqualTo(17));

                h.Data.AddReservedInbound(def, -99);
                Assert.That(h.Data.HasAnyReserved(), Is.False);
                Assert.That(h.Data.GetReservedInbound(def), Is.EqualTo(0));
                Assert.That(h.Data.GetEffectiveCount(def, default), Is.EqualTo(12));
            }
        }

        [Test]
        public void ReservedInboundIsIgnoredWhenGlobalSettingIsOff()
        {
            using (new PscSettingsScope(s => s.reservedFillCounting = false))
            {
                var def = TestDefs.Thing("PSC_TestReservedOff", stackLimit: 75);
                var h = new StorageDataHarness();
                h.SetCount(def, items: 12, physicalStacks: 1);
                h.Data.AddReservedInbound(def, 7);

                Assert.That(h.Data.GetEffectiveCount(def, default), Is.EqualTo(12));
            }
        }
    }
}
