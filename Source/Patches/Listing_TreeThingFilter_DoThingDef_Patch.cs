using HarmonyLib;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Depth-scaling per-item UI (design §10.4). Left-click/drag stay pure vanilla (allow/disallow).
    // PSC owns the RIGHT button on a row: a right-tap opens the limit submenu; a right-drag
    // propagates the start row's limit across rows. The read-only compact limit label is drawn so
    // effects are visible at a glance.
    //
    // vanilla DoThingDef calls EndLine() before returning (advancing curY to the next row), so the
    // prefix captures the row's own Y into __state for both the interaction rect and the postfix
    // label. All UI work is guarded so it can never break the vanilla filter.
    [HarmonyPatch(typeof(Listing_TreeThingFilter), "DoThingDef")]
    public static class Listing_TreeThingFilter_DoThingDef_Patch
    {
        private static readonly AccessTools.FieldRef<Listing, float> CurYRef =
            AccessTools.FieldRefAccess<Listing, float>("curY");

        private static readonly Color LimitColor = new Color(0.45f, 0.8f, 1f);

        private static Rect RowRect(Listing_TreeThingFilter inst, float rowY)
            => new Rect(0f, rowY, inst.ColumnWidth, ((Listing_Lines)inst).lineHeight);

        // Right mouse-DOWN over a row begins the right-button interaction (tap or drag). __state
        // carries the row's top Y to the postfix (curY has advanced by then).
        public static void Prefix(Listing_TreeThingFilter __instance, ThingDef tDef, out float __state)
        {
            __state = CurYRef(__instance);
            if (!PscUiContext.Active || tDef == null) return;
            var e = Event.current;
            if (e == null || e.type != EventType.MouseDown || e.button != 1) return;
            try
            {
                if (!RowRect(__instance, __state).Contains(e.mousePosition)) return;
                PscFilterPaint.BeginRight(PscUiContext.Settings, PscUiContext.Unit, tDef,
                    PscUiContext.Data?.GetLimit(tDef), e.mousePosition);
                e.Use();
            }
            catch { }
        }

        // Postfix: continue a right-drag, finish on right-up, and draw the read-only limit label.
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
                    else if (e.type == EventType.MouseUp && e.button == 1)
                    {
                        PscFilterPaint.EndRight();
                        e.Use();
                    }
                }

                var data = PscUiContext.Data;
                if (data != null && data.HasLimit(tDef))
                {
                    var rect = new Rect(0f, __state, __instance.ColumnWidth - 60f, ((Listing_Lines)__instance).lineHeight);
                    var prevFont = Text.Font;
                    var prevAnchor = Text.Anchor;
                    var prevColor = GUI.color;
                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.MiddleRight;
                    GUI.color = LimitColor;
                    Widgets.Label(rect, FormatCompact(data.GetLimit(tDef)));
                    Text.Font = prevFont;
                    Text.Anchor = prevAnchor;
                    GUI.color = prevColor;
                }
            }
            catch { }
        }

        private static string FormatCompact(PscDefLimit lim)
        {
            string lo = lim.Lower.HasValue ? lim.Lower.Value.ToString() : "";
            string hi = lim.Upper.HasValue ? lim.Upper.Value.ToString() : "";
            return lo + "–" + hi;   // en-dash: "5–20", "–20", "5–"
        }
    }
}
