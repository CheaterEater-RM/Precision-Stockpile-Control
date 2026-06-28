using NUnit.Framework;

namespace PrecisionStockpileControl.Tests
{
    [TestFixture]
    public sealed class AdmissionSourceGuardTests
    {
        [Test]
        public void AllowedToAcceptPatchStaysThingOverloadPostfixAndTightenOnly()
        {
            string src = RepoPaths.Read("Source", "Patches", "Admission_Patches.cs");

            Assert.That(src, Does.Contain("[HarmonyPatch(typeof(StorageSettings), nameof(StorageSettings.AllowedToAccept), new[] { typeof(Thing) })]"));
            RepoPaths.AssertInOrder(src,
                "if (PscEngineScope.BypassAdmissionBackstop) return;",
                "if (!__result) return;",
                "if (PscStorageDataStore.IsEmpty) return;",
                "if (t == null) return;",
                "PscAdmissionIndex.HardReject",
                "__result = false;");
            Assert.That(src, Does.Not.Contain("__result = true"));
        }

        [Test]
        public void HardRejectKeepsDocumentedReasonBranches()
        {
            string src = RepoPaths.Read("Source", "Core", "PscAdmissionIndex.cs");

            foreach (string reason in new[]
            {
                "\"modeNoIntake\"",
                "\"feeder\"",
                "\"underBatchEmpty\"",
                "\"overCapDrain\"",
                "\"overCap\"",
                "\"hysteresis\"",
                "\"underBatch\"",
                "\"underBatchRoom\"",
            })
                Assert.That(src, Does.Contain(reason), "Missing admission reason " + reason);
        }

        [Test]
        public void HardRejectOwnContentsExemptionHasOnlyStrictOverCapDrainCarveout()
        {
            string src = RepoPaths.Read("Source", "Core", "PscAdmissionIndex.cs");

            RepoPaths.AssertInOrder(src,
                "if (sourceIsTarget)",
                "data.TryGetDrainExcess(t.def, unit, out int drainExcess)",
                "reason = \"overCapDrain\"",
                "return true;",
                "return false;                            // own contents otherwise always valid (D16)");
        }

        [Test]
        public void HardRejectUsesEffectiveCountsOnlyForPlanningAndPhysicalCountsForRechecks()
        {
            string src = RepoPaths.Read("Source", "Core", "PscAdmissionIndex.cs");

            Assert.That(src, Does.Contain("int n = planning ? data.GetGroupAwareEffectiveCount(t.def, unit) : data.GetGroupAwareCount(t.def, unit);"));
            Assert.That(src, Does.Contain("data.GroupAwareItemRoom(t.def, unit, blim.Upper.Value, includeReserved: planning)"));
        }

        [Test]
        public void StacksGroupAdmissionRemainsCellAwareAtCap()
        {
            string src = RepoPaths.Read("Source", "Core", "PscAdmissionIndex.cs");

            RepoPaths.AssertInOrder(src,
                "grp != null && grp.countMode == PscGroupCountMode.Stacks",
                "int cells = data.GetGroupPhysicalStackCount(grp, unit);",
                "cells > lim.Upper.Value",
                "cells == lim.Upper.Value && !data.GroupDefHasMergeRoom(t.def, unit)");
        }

        [Test]
        public void BatchEmptyKeepsSourceAcceptsItemExemption()
        {
            string src = RepoPaths.Read("Source", "Core", "PscAdmissionIndex.cs");

            RepoPaths.AssertInOrder(src,
                "sourceData.batchEmpty > 0",
                "t.stackCount < sourceData.batchEmpty",
                "SourceAcceptsItem(source, t, planning)",
                "reason = \"underBatchEmpty\"");
        }
    }
}
