using HarmonyLib;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Persistence seam (design §4.1, §7). Scribes PSC data as a <psc> child node of the vanilla
    // StorageSettings node.
    //  - Saving: write nothing when there is no PSC policy -> removed-mod saves stay clean and
    //    adding the mod to an existing save is a no-op until the player interacts with it.
    //  - Loading: reconstruct and register in the global store. Vanilla ignores the unknown <psc>
    //    node if the mod is later removed.
    [HarmonyPatch(typeof(StorageSettings), nameof(StorageSettings.ExposeData))]
    public static class StorageSettings_ExposeData_Patch
    {
        public static void Postfix(StorageSettings __instance)
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                var data = PscStorageDataStore.TryGet(__instance);
                if (data == null || !data.HasPersistentPolicy) return;
                Scribe_Deep.Look(ref data, "psc");
            }
            else if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                PscStorageData loaded = null;
                Scribe_Deep.Look(ref loaded, "psc");
                if (loaded != null) { PscStorageDataStore.Set(__instance, loaded); return; }

                // No PSC node on this storage — if a removed limit mod left its own node behind in
                // the same vanilla <settings> parent, capture it now for one-way migration (resolved
                // post-load in PscGameComponent.FinalizeInit). A present <psc> always wins, so this
                // only runs the first time PSC sees a save that still carries a foreign node.
                PscMigration.TryCaptureForeign(__instance);
            }
        }
    }

    // Copy/paste + link-init seam (design §7, §11.6). StorageSettings.CopyFrom is vanilla's general
    // settings-transfer point: player copy/paste (StorageSettingsClipboard), StorageGroup.InitFrom on
    // link, and also build-finish (Frame.CompleteConstruction), caravan pack/unpack (Moveable*), and
    // def-init defaults (Building_Storage.PostMake from defaultStorageSettings). One postfix deep-
    // copies PSC policy from source to target (never counts — those are unit-specific) on all of them.
    // Pasting/initialising from PSC-free settings clears the target's PSC policy. Safe on the non-paste
    // paths: an empty source is a no-op Remove, and NotifyPolicyChanged no-ops when owner is unresolved
    // (def-load / clipboard).
    [HarmonyPatch(typeof(StorageSettings), nameof(StorageSettings.CopyFrom))]
    public static class StorageSettings_CopyFrom_Patch
    {
        public static void Postfix(StorageSettings __instance, StorageSettings other)
        {
            var src = PscStorageDataStore.TryGet(other);
            if (src == null || !src.HasPersistentPolicy)
            {
                PscStorageDataStore.Remove(__instance);
            }
            else
            {
                var data = PscStorageDataStore.GetOrCreate(__instance);
                data.CopyPolicyFrom(src);
                // CopyPolicyFrom clears the runtime refill (hysteresis) state. Re-derive it from the
                // target's current contents (paste / StorageGroup link / build-finish / caravan unpack
                // all route through CopyFrom), or a between-thresholds pile would be left stuck
                // not-refilling. No-op for clipboard / def-init targets whose owner doesn't resolve.
                var unit = PscHaulUnit.ResolveSettings(__instance);
                if (unit.IsValid) data.Notify_LimitsSeeded(unit);
            }
            PscMapComponent.NotifyPolicyChanged(__instance);
        }
    }
}
