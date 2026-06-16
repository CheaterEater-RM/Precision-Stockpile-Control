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
        private static readonly AccessTools.FieldRef<Listing, float> CurYRef =
            AccessTools.FieldRefAccess<Listing, float>("curY");

        private static Rect RowRect(Listing_TreeThingFilter inst, float rowY)
            => new Rect(0f, rowY, inst.ColumnWidth, ((Listing_Lines)inst).lineHeight);

        private static Rect CheckboxRect(Listing_TreeThingFilter inst, float rowY)
        {
            float lh = ((Listing_Lines)inst).lineHeight;
            return new Rect(inst.ColumnWidth - 26f, rowY, lh, lh);
        }

        private static void OpenItemMenu(ThingDef tDef)
        {
            Find.WindowStack.WindowOfType<PscItemLimitMenu>()?.Close(false);
            Find.WindowStack.Add(new PscItemLimitMenu(PscUiContext.Settings, PscUiContext.Unit,
                new System.Collections.Generic.List<ThingDef> { tDef }, tDef.LabelCap));
        }

        public static void Prefix(Listing_TreeThingFilter __instance, ThingDef tDef, out float __state)
        {
            __state = CurYRef(__instance);
            PscFilterPaint.ClearOwnedCheckbox();
            if (!PscUiContext.Active || tDef == null) return;
            var e = Event.current;
            try
            {
                var limit = PscUiContext.Data?.GetLimit(tDef);
                bool hasLimit = limit != null && !limit.IsDefault;
                var checkRect = CheckboxRect(__instance, __state);

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
                if (e.button == 1 && RowRect(__instance, __state).Contains(e.mousePosition))
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
                    if (e.type == EventType.MouseDrag && RowRect(__instance, __state).Contains(e.mousePosition))
                        PscFilterPaint.PaintRow(tDef, e.mousePosition);
                    else if (e.type == EventType.MouseUp && e.button == PscFilterPaint.Button)
                    {
                        PscFilterPaint.End();
                        e.Use();
                    }
                }

                var data = PscUiContext.Data;
                if (data == null || !data.HasLimit(tDef)) return;

                // A vanilla left-drag allow/disallow paint passing over a limited row overwrites the
                // precise limit with the plain painted state. Limited rows keep the vanilla checkbox
                // suppressed, so we replicate the paint here via ClearLimit (removes limit + SetAllow).
                if (PscFilterPaint.VanillaPaintActive && Mouse.IsOver(CheckboxRect(__instance, __state)))
                {
                    PscEdit.ClearLimit(PscUiContext.Settings, tDef, PscFilterPaint.VanillaPaintAllow);
                    PscFilterPaint.MarkVanillaPaintDirty(PscUiContext.Settings);
                    return; // limit cleared; the row reverts to a normal vanilla checkbox next frame
                }

                var lim = data.GetLimit(tDef);
                float lh = ((Listing_Lines)__instance).lineHeight;
                var iconRect = CheckboxRect(__instance, __state);
                var labelRect = new Rect(iconRect.xMin - 132f, __state, 128f, lh);

                var prevFont = Text.Font;
                var prevAnchor = Text.Anchor;
                var prevColor = GUI.color;
                try
                {
                    GUI.color = new Color(0f, 0f, 0f, 0.45f);
                    GUI.DrawTexture(labelRect.ContractedBy(0f, 2f), BaseContent.WhiteTex);
                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.MiddleRight;
                    GUI.color = PscUiWidgets.LimitTextColor;
                    string compact = PscUiWidgets.CompactLimit(lim, tDef);
                    Widgets.Label(labelRect, compact.Truncate(labelRect.width));
                    TooltipHandler.TipRegion(labelRect, PscUiWidgets.FullLimit(lim, tDef));
                    PscUiWidgets.DrawLimitMarker(iconRect);
                }
                finally
                {
                    Text.Font = prevFont;
                    Text.Anchor = prevAnchor;
                    GUI.color = prevColor;
                }
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
