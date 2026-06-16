using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Suppresses the single vanilla checkbox that sits under a PSC limit marker. This MUST cancel the
    // vanilla call rather than just over-draw the marker: Widgets.Checkbox consumes the click internally,
    // so over-drawing alone would still let a left-click on the marker toggle allow/disallow underneath.
    // Cancelling prefixes on a hot, game-wide vanilla method are a known conflict risk (CLAUDE.md Rule #6),
    // so the prefix early-outs to a no-op whenever PSC is not actively drawing an owned checkbox this frame
    // — every other checkbox in the game pays only that one boolean check. Do not "simplify" the
    // suppression away; the over-draw-only approach was tried and let clicks fall through.
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
            if (!PscUiContext.Active || !PscFilterPaint.HasOwnedCheckbox) return true;
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
            if (!PscUiContext.Active || !PscFilterPaint.HasOwnedCheckbox) return true;
            if (!PscFilterPaint.ShouldSuppressCheckbox(rect)) return true;
            __result = state;
            return false;
        }
    }
}
