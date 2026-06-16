using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Depth-scaling category UI (design §10.4). On a category row: a small I-beam badge when its
    // limited items have MIXED limits, or the shared limit text when they all share one; a
    // right-click opens the limit submenu scoped to the category's storable descendant defs (so a
    // category limit propagates to all children). Drawn in the row's right margin — never over the
    // vanilla allow-state checkbox. The prefix captures the row Y in __state so the postfix can
    // overdraw at the category row after vanilla has drawn it (immediate-mode GUI).
    [HarmonyPatch(typeof(Listing_TreeThingFilter), "DoCategory")]
    public static class Listing_TreeThingFilter_DoCategory_Patch
    {
        private static readonly AccessTools.FieldRef<Listing, float> CurYRef =
            AccessTools.FieldRefAccess<Listing, float>("curY");

        private static readonly Color LimitColor = new Color(0.45f, 0.8f, 1f);

        public static void Prefix(Listing_TreeThingFilter __instance, TreeNode_ThingCategory node, out float __state)
        {
            __state = CurYRef(__instance);
            if (!PscUiContext.Active || node?.catDef == null) return;

            var e = Event.current;
            if (e == null || e.type != EventType.MouseDown || e.button != 1) return;
            try
            {
                var rowRect = new Rect(0f, __state, __instance.ColumnWidth, ((Listing_Lines)__instance).lineHeight);
                if (!rowRect.Contains(e.mousePosition)) return;
                OpenCategoryMenu(node.catDef);
                e.Use();
            }
            catch { }
        }

        public static void Postfix(Listing_TreeThingFilter __instance, TreeNode_ThingCategory node, float __state)
        {
            if (!PscUiContext.Active || node?.catDef == null) return;
            var data = PscUiContext.Data;
            if (data == null || data.limits == null || data.limits.Count == 0) return;

            try
            {
                // Limit-state across this category's limited descendants (cheap: iterate the few
                // limited defs, not the whole subtree).
                PscDefLimit shared = null;
                bool mixed = false, any = false;
                foreach (var kv in data.limits)
                {
                    if (kv.Value == null || kv.Value.IsDefault || kv.Key == null) continue;
                    if (!kv.Key.IsWithinCategory(node.catDef)) continue;
                    any = true;
                    if (shared == null) shared = kv.Value;
                    else if (shared.lowerRaw != kv.Value.lowerRaw || shared.upperRaw != kv.Value.upperRaw) { mixed = true; break; }
                }
                if (!any) return;

                float colW = __instance.ColumnWidth;
                float lh = ((Listing_Lines)__instance).lineHeight;
                if (mixed)
                {
                    var box = new Rect(colW - 74f, __state + (lh - 12f) / 2f, 12f, 12f);
                    DrawIBeam(box, LimitColor);
                }
                else
                {
                    var rect = new Rect(0f, __state, colW - 60f, lh);
                    var pf = Text.Font; var pa = Text.Anchor; var pc = GUI.color;
                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.MiddleRight;
                    GUI.color = LimitColor;
                    Widgets.Label(rect, Compact(shared));
                    Text.Font = pf; Text.Anchor = pa; GUI.color = pc;
                }
            }
            catch { }
        }

        private static void OpenCategoryMenu(ThingCategoryDef cat)
        {
            ThingFilter parentFilter = PscUiContext.Settings?.owner?.GetParentStoreSettings()?.filter;
            var defs = new List<ThingDef>();
            foreach (var d in cat.DescendantThingDefs)
            {
                if (!d.EverStorable(false)) continue;
                if (parentFilter != null && !parentFilter.Allows(d)) continue;
                defs.Add(d);
            }
            if (defs.Count == 0) return;
            Find.WindowStack.WindowOfType<PscItemLimitMenu>()?.Close(false);
            Find.WindowStack.Add(new PscItemLimitMenu(PscUiContext.Settings, PscUiContext.Unit, defs, cat.LabelCap));
        }

        // ⊤—⊥ style limiting glyph drawn with GUI primitives (no texture asset).
        private static void DrawIBeam(Rect box, Color color)
        {
            var prev = GUI.color;
            GUI.color = color;
            float cx = box.center.x;
            float top = box.yMin, bot = box.yMax;
            const float t = 2f;
            float halfW = box.width * 0.5f;
            GUI.DrawTexture(new Rect(cx - halfW, top, halfW * 2f, t), BaseContent.WhiteTex);          // top bar
            GUI.DrawTexture(new Rect(cx - halfW, bot - t, halfW * 2f, t), BaseContent.WhiteTex);       // bottom bar
            GUI.DrawTexture(new Rect(cx - t / 2f, top, t, box.height), BaseContent.WhiteTex);          // vertical
            GUI.color = prev;
        }

        private static string Compact(PscDefLimit lim)
        {
            string lo = lim.Lower.HasValue ? lim.Lower.Value.ToString() : "";
            string hi = lim.Upper.HasValue ? lim.Upper.Value.ToString() : "";
            return lo + "–" + hi;
        }
    }
}
