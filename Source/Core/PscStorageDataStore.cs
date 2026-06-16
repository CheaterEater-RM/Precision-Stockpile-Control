using System.Collections.Generic;
using RimWorld;

namespace PrecisionStockpileControl
{
    // The single global map: vanilla StorageSettings object -> PSC policy/cache. Rebuildable
    // runtime state (allowed per the architecture rules — not durable static game state). Cleared
    // each load by PscGameComponent and repopulated by the StorageSettings.ExposeData postfix.
    //
    // IsEmpty is the cheapest hot-path early-out: when no PSC data exists anywhere, every patch
    // returns after a single int compare.
    public static class PscStorageDataStore
    {
        private static readonly Dictionary<StorageSettings, PscStorageData> map
            = new Dictionary<StorageSettings, PscStorageData>();

        public static bool IsEmpty => map.Count == 0;
        public static int Count => map.Count;

        public static PscStorageData TryGet(StorageSettings settings)
        {
            if (settings == null) return null;
            return map.TryGetValue(settings, out var d) ? d : null;
        }

        public static PscStorageData GetOrCreate(StorageSettings settings)
        {
            if (settings == null) return null;
            if (!map.TryGetValue(settings, out var d))
            {
                d = new PscStorageData();
                map[settings] = d;
            }
            return d;
        }

        public static void Set(StorageSettings settings, PscStorageData data)
        {
            if (settings == null || data == null) return;
            map[settings] = data;
        }

        public static void Remove(StorageSettings settings)
        {
            if (settings != null) map.Remove(settings);
        }

        public static void Clear() => map.Clear();

        // Snapshot-friendly enumeration for the per-map resync sweep.
        public static IEnumerable<KeyValuePair<StorageSettings, PscStorageData>> All => map;
    }
}
