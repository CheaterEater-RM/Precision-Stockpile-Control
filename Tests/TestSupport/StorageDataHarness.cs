using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace PrecisionStockpileControl.Tests
{
    internal sealed class StorageDataHarness
    {
        private static readonly FieldInfo CountsField = typeof(PscStorageData).GetField(
            "counts", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PhysicalStackCountsField = typeof(PscStorageData).GetField(
            "physicalStackCounts", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo DirtyDefsField = typeof(PscStorageData).GetField(
            "dirtyDefs", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo AllDirtyField = typeof(PscStorageData).GetField(
            "allDirty", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo DefToGroupField = typeof(PscStorageData).GetField(
            "defToGroup", BindingFlags.Instance | BindingFlags.NonPublic);

        public PscStorageData Data { get; } = new PscStorageData();

        public StorageDataHarness()
        {
            Clean();
        }

        public void SetCount(ThingDef def, int items, int physicalStacks)
        {
            Counts[def] = items;
            PhysicalStackCounts[def] = physicalStacks;
            Clean();
        }

        public void SetPerDefLimit(ThingDef def, int? upper = null, int? lower = null)
        {
            var limit = new PscDefLimit { Lower = lower, Upper = upper };
            Data.limits[def] = limit;
            Clean();
        }

        public PscLimitGroup AddGroup(PscGroupCountMode mode, int? upper, int? lower, params ThingDef[] members)
        {
            var limit = new PscDefLimit { Lower = lower, Upper = upper };
            var group = new PscLimitGroup { limit = limit, countMode = mode };
            group.members.AddRange(members);
            group.SyncNames();
            Data.limitGroups.Add(group);
            foreach (var member in members) DefToGroup[member] = group;
            Clean();
            return group;
        }

        public void Clean()
        {
            AllDirtyField.SetValue(Data, false);
            DirtyDefs.Clear();
        }

        private Dictionary<ThingDef, int> Counts
            => (Dictionary<ThingDef, int>)CountsField.GetValue(Data);

        private Dictionary<ThingDef, int> PhysicalStackCounts
            => (Dictionary<ThingDef, int>)PhysicalStackCountsField.GetValue(Data);

        private HashSet<ThingDef> DirtyDefs
            => (HashSet<ThingDef>)DirtyDefsField.GetValue(Data);

        private Dictionary<ThingDef, PscLimitGroup> DefToGroup
            => (Dictionary<ThingDef, PscLimitGroup>)DefToGroupField.GetValue(Data);
    }
}
