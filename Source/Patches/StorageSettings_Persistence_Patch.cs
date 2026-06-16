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
                if (loaded != null) PscStorageDataStore.Set(__instance, loaded);
            }
        }
    }

    // Copy/paste + link-init seam (design §7, §11.6). Both StorageSettingsClipboard.Copy and
    // .PasteInto route through CopyFrom, as does StorageGroup.InitFrom on link. One postfix deep-
    // copies PSC policy from source to target (never counts — those are unit-specific). Pasting
    // vanilla (PSC-free) settings clears the target's PSC policy.
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
                PscStorageDataStore.GetOrCreate(__instance).CopyPolicyFrom(src);
            }
            PscMapComponent.NotifyPolicyChanged(__instance);
        }
    }
}
