using HarmonyLib;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    [HarmonyPatch(typeof(ThingFilterUI), "DoThingFilterConfigWindow")]
    public static class ThingFilterUI_Patch
    {
        public static void Prefix(ref Rect rect)
        {
            if (!PscUiContext.Active) return;
            rect.yMin += PscUiWidgets.StorageTabReserveHeight;
        }
    }
}
