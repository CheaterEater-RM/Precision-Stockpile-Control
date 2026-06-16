using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    [HarmonyPatch]
    public static class Widgets_Checkbox_Patch
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Widgets), nameof(Widgets.Checkbox), new[]
            {
                typeof(float),
                typeof(float),
                typeof(bool).MakeByRefType(),
                typeof(float),
                typeof(bool),
                typeof(bool),
                typeof(Texture2D),
                typeof(Texture2D)
            });
        }

        public static bool Prefix(float x, float y, float size)
        {
            return !PscFilterPaint.ShouldSuppressCheckbox(new Rect(x, y, size, size));
        }
    }

    [HarmonyPatch]
    public static class Widgets_CheckboxMulti_Patch
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Widgets), nameof(Widgets.CheckboxMulti), new[]
            {
                typeof(Rect),
                typeof(MultiCheckboxState),
                typeof(bool)
            });
        }

        public static bool Prefix(Rect rect, MultiCheckboxState state, ref MultiCheckboxState __result)
        {
            if (!PscFilterPaint.ShouldSuppressCheckbox(rect)) return true;
            __result = state;
            return false;
        }
    }
}
