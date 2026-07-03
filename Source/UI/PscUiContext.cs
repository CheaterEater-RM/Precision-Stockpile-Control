using RimWorld;

namespace PrecisionStockpileControl
{
    // Scoped UI context for the per-row limit UI. The storage tab's row methods
    // (Listing_TreeThingFilter.DoThingDef / DoCategory) do not know which StorageSettings they
    // belong to, so the ITab_Storage.FillTab prefix stashes the active unit/data here for the
    // duration of the FillTab call and a finalizer clears it.
    //
    // Active is true whenever the PSC storage tab UI is shown — including on a storage BLUEPRINT /
    // construction frame, whose Unit is NOT yet live (no slot group). PSC has no runtime effect on
    // an unbuilt shelf; the tab edits pure StorageSettings policy that carries to the finished
    // building via StorageSettings.CopyFrom. Callers that need "is this a live, enforcing unit?"
    // (live capacity, enforcement) must check Unit.IsValid, NOT Active. Data may be null; the per-row
    // LABEL only draws when Data has a limit, so PSC-free storages still pay almost nothing.
    //
    // PreviewStackSlots is the built shelf's stack capacity derived from its BuildDef, stashed only
    // for an unbuilt (invalid) Unit so the limit sliders show the same ceiling they will once built;
    // 0 means "not a preview / use the live unit's capacity".
    //
    // UI runs single-threaded on the main thread, so a plain static is safe here.
    public static class PscUiContext
    {
        public static bool Active;
        public static StorageSettings Settings;
        public static PscStorageData Data;
        public static PscHaulUnit Unit;
        public static int PreviewStackSlots;

        public static void Set(StorageSettings settings, PscHaulUnit unit, int previewStackSlots = 0)
        {
            Settings = settings;
            Unit = unit;
            Data = PscStorageDataStore.TryGet(settings);
            PreviewStackSlots = previewStackSlots;
            Active = settings != null;
        }

        public static void Clear()
        {
            Active = false;
            Settings = null;
            Data = null;
            Unit = default;
            PreviewStackSlots = 0;
        }
    }
}
