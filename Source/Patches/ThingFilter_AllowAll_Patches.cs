using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    [HarmonyPatch(typeof(ThingFilter), nameof(ThingFilter.SetDisallowAll))]
    public static class ThingFilter_SetDisallowAll_Patch
    {
        public static void Postfix(ThingFilter __instance, IEnumerable<ThingDef> exceptedDefs = null,
            IEnumerable<SpecialThingFilterDef> exceptedFilters = null)
        {
            ClearActiveStorageLimits(__instance);
        }

        internal static void ClearActiveStorageLimits(ThingFilter filter)
        {
            if (!PscUiContext.Active || PscUiContext.Settings?.filter == null) return;
            if (!ReferenceEquals(filter, PscUiContext.Settings.filter)) return;
            PscEdit.ClearAllLimits(PscUiContext.Settings);
        }
    }

    [HarmonyPatch(typeof(ThingFilter), nameof(ThingFilter.SetAllowAll))]
    public static class ThingFilter_SetAllowAll_Patch
    {
        public static void Postfix(ThingFilter __instance, ThingFilter parentFilter, bool includeNonStorable = false)
        {
            ThingFilter_SetDisallowAll_Patch.ClearActiveStorageLimits(__instance);
        }
    }
}
