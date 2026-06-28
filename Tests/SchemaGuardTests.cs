using System.Linq;
using NUnit.Framework;

namespace PrecisionStockpileControl.Tests
{
    [TestFixture]
    public sealed class SchemaGuardTests
    {
        [Test]
        public void StorageDataAndDefLimitScribeNodeNamesAreLocked()
        {
            Assert.That(RepoPaths.ScribeNodes("Source", "Core", "PscStorageData.cs").Distinct().OrderBy(x => x),
                Is.EqualTo(new[]
                {
                    "alarm",
                    "batch",
                    "batchEmpty",
                    "groups",
                    "letter",
                    "limits",
                    "lower",
                    "mode",
                    "onlyFromSource",
                    "onlyToDestinations",
                    "perTileLimit",
                    "refill",
                    "subTier",
                    "upper",
                }.OrderBy(x => x)));
        }

        [Test]
        public void LimitGroupScribeNodeNamesAndMemberLookModeAreLocked()
        {
            string src = RepoPaths.Read("Source", "Core", "PscLimitGroup.cs");

            Assert.That(RepoPaths.ScribeNodes("Source", "Core", "PscLimitGroup.cs").Distinct().OrderBy(x => x),
                Is.EqualTo(new[] { "countMode", "letter", "limit", "members", "name" }.OrderBy(x => x)));
            Assert.That(src, Does.Contain("Scribe_Collections.Look(ref memberNames, \"members\", LookMode.Value)"));
            Assert.That(src, Does.Not.Contain("Scribe_Collections.Look(ref memberNames, \"members\", LookMode.Def)"));
        }

        [Test]
        public void AlarmScribeNodeNamesAreLocked()
        {
            Assert.That(RepoPaths.ScribeNodes("Source", "Core", "PscAlarmConfig.cs").Distinct().OrderBy(x => x),
                Is.EqualTo(new[]
                {
                    "highFired",
                    "highPct",
                    "highSince",
                    "lowFired",
                    "lowPct",
                    "lowSince",
                    "message",
                    "notify",
                    "repeat",
                    "sustainHours",
                }.OrderBy(x => x)));
        }

        [Test]
        public void FeederGraphScribeNodeNamesAreLocked()
        {
            Assert.That(RepoPaths.ScribeNodes("Source", "Core", "PscFeederLinks.cs").Distinct().OrderBy(x => x),
                Is.EqualTo(new[] { "dst", "links", "src" }.OrderBy(x => x)));
            Assert.That(RepoPaths.ScribeNodes("Source", "Core", "PscFeederManager.cs").Distinct(),
                Is.EqualTo(new[] { "feederLinks" }));
        }

        [Test]
        public void SettingsScribeNodeNamesAreLocked()
        {
            Assert.That(RepoPaths.ScribeNodes("Source", "PscMod.cs").Distinct().OrderBy(x => x),
                Is.EqualTo(new[]
                {
                    "autosetDestinationPriority",
                    "autosetSourcePriority",
                    "debugFeederVerbose",
                    "debugLogging",
                    "defaultOnlyFromSource",
                    "defaultOnlyToDestinations",
                    "feederChainHighlight",
                    "feederDirectionColor",
                    "feederDotsOnly",
                    "feederFlowDots",
                    "feederFocusDim",
                    "feederHashShading",
                    "feederLineWidth",
                    "feederPortSpreading",
                    "feederSkipHops",
                    "feederSkipLooseItems",
                    "perTileLimits",
                    "priorityNumbering",
                    "reservedFillCounting",
                    "reverseOrder",
                    "subpriorityLetters",
                }.OrderBy(x => x)));
        }

        [Test]
        public void StorageSettingsRideAlongNodeIsLockedToPsc()
        {
            Assert.That(RepoPaths.ScribeNodes("Source", "Patches", "StorageSettings_Persistence_Patch.cs").Distinct(),
                Is.EqualTo(new[] { "psc" }));
        }

        [Test]
        public void RemovalSafeGroupAndLimitPersistenceShapeIsGuarded()
        {
            string storageData = RepoPaths.Read("Source", "Core", "PscStorageData.cs");
            string group = RepoPaths.Read("Source", "Core", "PscLimitGroup.cs");

            Assert.That(storageData, Does.Contain("logNullErrors: false"));
            Assert.That(storageData, Does.Contain("if (limitGroups != null && limitGroups.Count > 0)"));
            Assert.That(storageData, Does.Contain("NormalizeGroups();"));
            Assert.That(group, Does.Contain("DefDatabase<ThingDef>.GetNamedSilentFail"));
        }
    }
}
