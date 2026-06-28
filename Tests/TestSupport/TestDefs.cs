using System;
using System.Reflection;
using System.Runtime.Serialization;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl.Tests
{
    internal static class TestDefs
    {
        private static ushort nextShortHash = 1;

        public static ThingDef Thing(string defName, int stackLimit = 75, ushort? shortHash = null)
        {
            var def = (ThingDef)FormatterServices.GetUninitializedObject(typeof(ThingDef));
            SetField(typeof(Def), def, "defName", defName);
            SetField(typeof(ThingDef), def, "stackLimit", stackLimit);
            SetShortHash(def, shortHash ?? nextShortHash++);
            return def;
        }

        // A fabricated spawned-less Thing carrying only the fields the admission path reads (def +
        // stackCount). Built with GetUninitializedObject so no Verse/Unity ctor or static init runs.
        public static Verse.Thing ItemOf(ThingDef def, int stackCount)
        {
            var t = (Verse.Thing)FormatterServices.GetUninitializedObject(typeof(Verse.Thing));
            t.def = def;
            t.stackCount = stackCount;
            return t;
        }

        // A bare StorageSettings reference for use ONLY as the PscStorageDataStore dictionary key in
        // HardReject tests (HardReject looks up its data via PscStorageDataStore.TryGet(target) and never
        // calls a method on target on the paths we exercise). No ctor/filter is needed.
        public static StorageSettings StorageKey()
            => (StorageSettings)FormatterServices.GetUninitializedObject(typeof(StorageSettings));

        private static void SetField(Type type, object target, string name, object value)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null) throw new MissingFieldException(type.FullName, name);
            field.SetValue(target, value);
        }

        private static void SetShortHash(ThingDef def, ushort value)
        {
            Type t = typeof(Def);
            var field = t.GetField("shortHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(def, value);
                return;
            }

            var property = t.GetProperty("shortHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                property.SetValue(def, value);
                return;
            }

            throw new MissingMemberException(typeof(Def).FullName, "shortHash");
        }
    }
}
