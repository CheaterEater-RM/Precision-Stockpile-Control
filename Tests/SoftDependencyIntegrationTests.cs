using System;
using System.Reflection;
using NUnit.Framework;

namespace PrecisionStockpileControl.Tests
{
    [TestFixture]
    public sealed class SoftDependencyIntegrationTests
    {
        [Test]
        public void PickUpAndHaulReflectionSeamsAreLockedAndSoft()
        {
            string src = RepoPaths.Read("Source", "Core", "PscReflection.cs");

            Assert.That(src, Does.Contain("public const string PuahCapacityAtId = \"PickUpAndHaul.WorkGiver_HaulToInventory:CapacityAt\";"));
            Assert.That(src, Does.Contain("new[] { typeof(Thing), typeof(IntVec3), typeof(Map) }"));
            Assert.That(src, Does.Contain("public const string PuahHaulToInventoryReserveId = \"PickUpAndHaul.JobDriver_HaulToInventory:TryMakePreToilReservations\";"));
            Assert.That(src, Does.Contain("public const string PuahExtraItemStoreCellId = \"PickUpAndHaul.WorkGiver_HaulToInventory:TryFindBestBetterStoreCellFor\";"));
            Assert.That(src, Does.Contain("ResolveTypeByName(\"PickUpAndHaul.WorkGiver_HaulToInventory\")"));
        }

        [Test]
        public void HaulersDreamReflectionSeamsAreLockedAndSoft()
        {
            string src = RepoPaths.Read("Source", "Core", "PscReflection.cs");

            Assert.That(src, Does.Contain("public const string HaulersDreamStorageSpaceForDefId = \"HaulersDream.BulkHaul:StorageSpaceForDef\";"));
            Assert.That(src, Does.Contain("new[] { typeof(Pawn), typeof(Thing), typeof(IntVec3), typeof(Map) }"));
            Assert.That(src, Does.Contain("public const string HaulersDreamBulkHaulReserveId = \"HaulersDream.JobDriver_BulkHaul:TryMakePreToilReservations\";"));
        }

        [Test]
        public void OptionalResolversReturnNullWhenBulkHaulModsAreAbsent()
        {
            Type reflection = typeof(PscMod).Assembly.GetType("PrecisionStockpileControl.PscReflection", throwOnError: true);

            Assert.That(InvokeResolver(reflection, "PuahCapacityAt"), Is.Null);
            Assert.That(InvokeResolver(reflection, "PuahHaulToInventoryReserve"), Is.Null);
            Assert.That(InvokeResolver(reflection, "PuahExtraItemStoreCell"), Is.Null);
            Assert.That(InvokeResolver(reflection, "HaulersDreamStorageSpaceForDef"), Is.Null);
            Assert.That(InvokeResolver(reflection, "HaulersDreamBulkHaulReserve"), Is.Null);
        }

        [Test]
        public void PickUpAndHaulCapacityClampUsesReservedRoomAndNeverRaisesCapacity()
        {
            string src = RepoPaths.Read("Source", "Patches", "PickUpAndHaul_Patch.cs");

            RepoPaths.AssertInOrder(src,
                "public static bool Prepare() => PscReflection.PuahCapacityAt() != null;",
                "public static MethodBase TargetMethod() => PscReflection.PuahCapacityAt();",
                "if (PscStorageDataStore.IsEmpty || __result <= 0 || thing == null) return;",
                "PscCap.TryGetRoom(storeCell, map, thing.def, out int room, includeReserved: true)",
                "room < __result",
                "__result = room");
        }

        [Test]
        public void PickUpAndHaulExtraItemAdapterDelegatesToPscEngineAndTearsDownSearchState()
        {
            string src = RepoPaths.Read("Source", "Patches", "PickUpAndHaul_Patch.cs");

            RepoPaths.AssertInOrder(src,
                "public static bool Prepare() => PscReflection.PuahExtraItemStoreCell() != null;",
                "if (PscStorageDataStore.IsEmpty || thing == null || map == null) return true;",
                "new PscSearchOptions(skip, needAccurateResult: true, PscSearchCaller.PuahExtraItem)",
                "PscStoreSearchEngine.TryFindBestStoreCell",
                "skip?.Add(cell);",
                "finally",
                "PscStoreSearchEngine.ResetSearchState();");
        }

        [Test]
        public void BulkHaulCaptureUsesCorrectTargetQueuesForPuahAndHaulersDream()
        {
            string puah = RepoPaths.Read("Source", "Patches", "PickUpAndHaul_Patch.cs");
            string hd = RepoPaths.Read("Source", "Patches", "HaulersDream_Patch.cs");

            Assert.That(puah, Does.Contain("PscCarriedSourceCapture.CaptureQueue(__instance, __result, TargetIndex.A)"));
            Assert.That(hd, Does.Contain("PscCarriedSourceCapture.CaptureQueue(__instance, __result, TargetIndex.B)"));
        }

        [Test]
        public void HaulersDreamCapacityClampUsesSameReservedRoomPolicyAsPuah()
        {
            string src = RepoPaths.Read("Source", "Patches", "HaulersDream_Patch.cs");

            RepoPaths.AssertInOrder(src,
                "public static bool Prepare() => PscReflection.HaulersDreamStorageSpaceForDef() != null;",
                "public static MethodBase TargetMethod() => PscReflection.HaulersDreamStorageSpaceForDef();",
                "if (PscStorageDataStore.IsEmpty || __result <= 0 || thing == null) return;",
                "PscCap.TryGetRoom(cell, map, thing.def, out int room, includeReserved: true)",
                "room < __result",
                "__result = room");
        }

        private static object InvokeResolver(Type reflectionType, string methodName)
        {
            MethodInfo method = reflectionType.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public);
            return method.Invoke(null, null);
        }
    }
}
