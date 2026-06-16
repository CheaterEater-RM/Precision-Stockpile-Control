using HarmonyLib;
using RimWorld;

namespace PrecisionStockpileControl
{
    // When the optional 1-10 numbering is on, PSC replaces the vanilla Priority band button with its
    // own level box (drawn by PscPriorityBox in the same footprint). Vanilla's button runs DURING
    // FillTab, before our postfix, so it can't merely be overdrawn — a single click would open both
    // the vanilla band menu and the PSC level menu. Instead we suppress it at the source:
    //
    //   - get_IsPrioritySettingVisible -> false (only true->false) skips vanilla's button block.
    //   - get_TopAreaHeight -> 35 keeps the top area at its priority-visible height so the filter
    //     list below doesn't shift up (TopAreaHeight = IsPrioritySettingVisible ? 35 : 20).
    //
    // Both are no-ops unless priorityNumbering is on, so with the toggle off the storage tab is
    // byte-identical to vanilla. UI is single-threaded, so the captured-original flag is safe.
    public static class ITab_Storage_PrioritySuppress_Patch
    {
        // The unpatched value of IsPrioritySettingVisible for the tab currently being filled. Tabs
        // like ITab_Shells / ITab_BiosculpterNutritionStorage legitimately return false; we must not
        // force a priority control onto those. Refreshed every time the getter is evaluated.
        internal static bool LastPriorityVisibleOriginal;

        private static bool NumberingOn => PscMod.Settings != null && PscMod.Settings.priorityNumbering;

        [HarmonyPatch(typeof(ITab_Storage), "IsPrioritySettingVisible", MethodType.Getter)]
        public static class IsPrioritySettingVisible_Patch
        {
            public static void Postfix(ref bool __result)
            {
                LastPriorityVisibleOriginal = __result;
                if (NumberingOn && __result)
                    __result = false; // hide vanilla's band button; PSC draws the level box instead
            }
        }

        [HarmonyPatch(typeof(ITab_Storage), "TopAreaHeight", MethodType.Getter)]
        public static class TopAreaHeight_Patch
        {
            public static void Postfix(ref float __result)
            {
                // TopAreaHeight's own body reads IsPrioritySettingVisible immediately before this
                // postfix, so LastPriorityVisibleOriginal is always current here regardless of where
                // FillTab calls TopAreaHeight. Restore the priority-visible height that our
                // IsPrioritySettingVisible suppression would otherwise have collapsed to 20.
                if (NumberingOn && LastPriorityVisibleOriginal)
                    __result = 35f;
            }
        }
    }
}
