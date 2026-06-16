using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Single place that mutates a unit's policy for one def + keeps the vanilla filter in sync.
    // Callers batch several defs then call PscMapComponent.NotifyPolicyChanged once.
    internal static class PscEdit
    {
        public static void ApplyLimit(StorageSettings settings, PscHaulUnit unit, ThingDef def, PscDefLimit lim)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            if (lim == null || lim.IsDefault) data.limits.Remove(def);
            else data.limits[def] = lim;
            settings.filter.SetAllow(def, true);
            data.Notify_LimitSet(def, unit);
        }

        public static void ClearLimit(StorageSettings settings, ThingDef def, bool allow)
        {
            PscStorageDataStore.TryGet(settings)?.limits.Remove(def);
            settings.filter.SetAllow(def, allow);
        }

        public static void ClearAllLimits(StorageSettings settings)
        {
            var data = PscStorageDataStore.TryGet(settings);
            if (data == null || data.limits.Count == 0) return;
            data.limits.Clear();
            PscMapComponent.NotifyPolicyChanged(settings);
        }
    }
}
