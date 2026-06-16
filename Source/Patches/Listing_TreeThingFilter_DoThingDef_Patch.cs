using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Depth-scaling per-item UI. Limited rows own the vanilla checkbox slot: left-click opens the
    // PSC editor and left-drag propagates the limit. Untouched rows remain pure vanilla.
    [HarmonyPatch(typeof(Listing_TreeThingFilter), "DoThingDef")]
    public static class Listing_TreeThingFilter_DoThingDef_Patch
    {
        private static void OpenItemMenu(ThingDef tDef)
        {
            Find.WindowStack.WindowOfType<PscItemLimitMenu>()?.Close(false);
            Find.WindowStack.Add(new PscItemLimitMenu(PscUiContext.Settings, PscUiContext.Unit,
                new System.Collections.Generic.List<ThingDef> { tDef }, tDef.LabelCap));
        }

        public static void Prefix(Listing_TreeThingFilter __instance, ThingDef tDef, out float __state)
        {
            __state = PscReflection.GetListingCurY(__instance);
            PscFilterPaint.ClearOwnedCheckbox();
            if (!PscUiContext.Active || tDef == null) return;
            var e = Event.current;
            try
            {
                var limit = PscUiContext.Data?.GetLimit(tDef);
                bool hasLimit = limit != null && !limit.IsDefault;
                var checkRect = PscFilterRow.CheckboxRect(__instance, __state);

                if (hasLimit)
                {
                    PscFilterPaint.OwnCheckbox(checkRect);
                }

                if (e == null || e.type != EventType.MouseDown) return;

                if (hasLimit && e.button == 0 && checkRect.Contains(e.mousePosition))
                {
                    PscFilterPaint.Begin(0, PscUiContext.Settings, PscUiContext.Unit, tDef, limit, e.mousePosition);
                    e.Use();
                    return;
                }

                // Right-click opens the per-item menu directly on MouseDown (mirrors DoCategory).
                // Deferring to MouseUp via the paint dance was unreliable, so the menu could be lost.
                if (e.button == 1 && PscFilterRow.RowRect(__instance, __state).Contains(e.mousePosition))
                {
                    OpenItemMenu(tDef);
                    e.Use();
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[PSC] DoThingDef prefix failed: " + ex, 0x1C5A0001);
            }
        }

        public static void Postfix(Listing_TreeThingFilter __instance, ThingDef tDef, float __state)
        {
            if (!PscUiContext.Active || tDef == null) return;
            try
            {
                var e = Event.current;
                if (e != null && PscFilterPaint.Active)
                {
                    if (e.type == EventType.MouseDrag && PscFilterRow.RowRect(__instance, __state).Contains(e.mousePosition))
                        PscFilterPaint.PaintRow(tDef, e.mousePosition);
                    else if (e.type == EventType.MouseUp && e.button == PscFilterPaint.Button)
                    {
                        PscFilterPaint.End();
                        e.Use();
                    }
                }

                var data = PscUiContext.Data;
                if (data == null || !data.HasLimit(tDef)) return;

                var iconRect = PscFilterRow.CheckboxRect(__instance, __state);

                // A vanilla left-drag allow/disallow paint passing over a limited row overwrites the
                // precise limit with the plain painted state. Limited rows keep the vanilla checkbox
                // suppressed, so we replicate the paint here via ClearLimit (removes limit + SetAllow).
                if (PscFilterPaint.VanillaPaintActive && Mouse.IsOver(iconRect))
                {
                    PscEdit.ClearLimit(PscUiContext.Settings, tDef, PscFilterPaint.VanillaPaintAllow);
                    PscFilterPaint.MarkVanillaPaintDirty(PscUiContext.Settings);
                    return; // limit cleared; the row reverts to a normal vanilla checkbox next frame
                }

                var lim = data.GetLimit(tDef);
                float lh = ((Listing_Lines)__instance).lineHeight;
                PscFilterRow.DrawLimitLabel(iconRect, __state, lh,
                    PscUiWidgets.CompactLimit(lim, tDef), PscUiWidgets.FullLimit(lim, tDef));
                PscUiWidgets.DrawLimitMarker(iconRect);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[PSC] DoThingDef postfix failed: " + ex, 0x1C5A0002);
            }
            finally
            {
                PscFilterPaint.ClearOwnedCheckbox();
            }
        }
    }
}
