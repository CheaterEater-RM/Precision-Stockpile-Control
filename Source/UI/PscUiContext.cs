using RimWorld;

namespace PrecisionStockpileControl
{
    // Scoped UI context for the per-row limit UI. The storage tab's row methods
    // (Listing_TreeThingFilter.DoThingDef / DoCategory) do not know which StorageSettings they
    // belong to, so the ITab_Storage.FillTab prefix stashes the active unit/data here for the
    // duration of the FillTab call and a finalizer clears it.
    //
    // Active is true whenever a storage tab is open with a resolvable unit — even with no PSC data
    // yet — so right-click can create a first limit. Data may be null; the per-row LABEL only draws
    // when Data has a limit, so PSC-free storages still pay almost nothing.
    //
    // UI runs single-threaded on the main thread, so a plain static is safe here.
    public static class PscUiContext
    {
        public static bool Active;
        public static StorageSettings Settings;
        public static PscStorageData Data;
        public static PscHaulUnit Unit;

        public static void Set(StorageSettings settings, PscHaulUnit unit)
        {
            Settings = settings;
            Unit = unit;
            Data = PscStorageDataStore.TryGet(settings);
            Active = settings != null && unit.IsValid;
        }

        public static void Clear()
        {
            Active = false;
            Settings = null;
            Data = null;
            Unit = default;
        }
    }
}
