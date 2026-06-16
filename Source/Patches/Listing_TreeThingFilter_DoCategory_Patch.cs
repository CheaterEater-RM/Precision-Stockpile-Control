using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Category rows summarize PSC state across currently allowed, storable descendants. Disallowed
    // descendants do not prevent a category from showing a shared range.
    [HarmonyPatch(typeof(Listing_TreeThingFilter), "DoCategory")]
    public static class Listing_TreeThingFilter_DoCategory_Patch
    {
        private static readonly AccessTools.FieldRef<Listing, float> CurYRef =
            AccessTools.FieldRefAccess<Listing, float>("curY");

        private static Rect CheckboxRect(Listing_TreeThingFilter inst, float rowY)
        {
            float lh = ((Listing_Lines)inst).lineHeight;
            return new Rect(inst.ColumnWidth - 26f, rowY, lh, lh);
        }

        public static void Prefix(Listing_TreeThingFilter __instance, TreeNode_ThingCategory node, out float __state)
        {
            __state = CurYRef(__instance);
            PscFilterPaint.ClearOwnedCheckbox();
            if (!PscUiContext.Active || node?.catDef == null) return;

            var e = Event.current;
            try
            {
                bool hasPscState = TryGetCategoryLimitState(node.catDef, out _, out _);
                if (hasPscState)
                {
                    var checkRect = CheckboxRect(__instance, __state);
                    PscFilterPaint.OwnCheckbox(checkRect);
                    if (e != null && e.type == EventType.MouseDown && e.button == 0 && checkRect.Contains(e.mousePosition))
                    {
                        OpenCategoryMenu(node.catDef);
                        e.Use();
                        return;
                    }
                }

                if (e == null || e.type != EventType.MouseDown || e.button != 1) return;
                var rowRect = new Rect(0f, __state, __instance.ColumnWidth, ((Listing_Lines)__instance).lineHeight);
                if (rowRect.Contains(e.mousePosition))
                {
                    OpenCategoryMenu(node.catDef);
                    e.Use();
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[PSC] DoCategory prefix failed: " + ex, 0x1C5A0003);
            }
        }

        public static void Postfix(Listing_TreeThingFilter __instance, TreeNode_ThingCategory node, float __state)
        {
            if (!PscUiContext.Active || node?.catDef == null) return;
            try
            {
                // PSC marker drag (started on an item with a limit) propagating onto a category applies
                // the dragged limit to every storable descendant. Handled before the PSC-state check so
                // a limit can be pasted onto a category that has no limits yet.
                var e = Event.current;
                if (e != null && PscFilterPaint.Active)
                {
                    var rowRect = new Rect(0f, __state, __instance.ColumnWidth, ((Listing_Lines)__instance).lineHeight);
                    if (e.type == EventType.MouseDrag && rowRect.Contains(e.mousePosition))
                    {
                        foreach (var d in StorableDescendants(node.catDef))
                            PscFilterPaint.PaintRow(d, e.mousePosition);
                    }
                    else if (e.type == EventType.MouseUp && e.button == PscFilterPaint.Button)
                    {
                        PscFilterPaint.End();
                        e.Use();
                    }
                }

                if (!TryGetCategoryLimitState(node.catDef, out var shared, out bool mixed, out int? sharedStackLimit)) return;

                // A vanilla left-drag allow/disallow paint over a category overwrites every descendant's
                // limit with the plain painted state (mirrors vanilla's category-paint cascade). The
                // category checkbox is suppressed while it has PSC state, so we apply it ourselves.
                if (PscFilterPaint.VanillaPaintActive && Mouse.IsOver(CheckboxRect(__instance, __state)))
                {
                    bool allow = PscFilterPaint.VanillaPaintAllow;
                    foreach (var d in StorableDescendants(node.catDef))
                        PscEdit.ClearLimit(PscUiContext.Settings, d, allow);
                    PscFilterPaint.MarkVanillaPaintDirty(PscUiContext.Settings);
                    return;
                }

                float lh = ((Listing_Lines)__instance).lineHeight;
                var iconRect = CheckboxRect(__instance, __state);
                PscUiWidgets.DrawLimitMarker(iconRect);

                if (!mixed)
                {
                    var labelRect = new Rect(iconRect.xMin - 132f, __state, 128f, lh);
                    var pf = Text.Font;
                    var pa = Text.Anchor;
                    var pc = GUI.color;
                    try
                    {
                        GUI.color = new Color(0f, 0f, 0f, 0.45f);
                        GUI.DrawTexture(labelRect.ContractedBy(0f, 2f), BaseContent.WhiteTex);
                        Text.Font = GameFont.Tiny;
                        Text.Anchor = TextAnchor.MiddleRight;
                        GUI.color = PscUiWidgets.LimitTextColor;
                        Widgets.Label(labelRect, PscUiWidgets.CompactLimit(shared, sharedStackLimit).Truncate(labelRect.width));
                        TooltipHandler.TipRegion(labelRect, PscUiWidgets.FullLimit(shared, sharedStackLimit));
                    }
                    finally
                    {
                        Text.Font = pf;
                        Text.Anchor = pa;
                        GUI.color = pc;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[PSC] DoCategory postfix failed: " + ex, 0x1C5A0004);
            }
            finally
            {
                PscFilterPaint.ClearOwnedCheckbox();
            }
        }

        private static bool TryGetCategoryLimitState(ThingCategoryDef cat, out PscDefLimit shared, out bool mixed)
        {
            return TryGetCategoryLimitState(cat, out shared, out mixed, out _);
        }

        private static bool TryGetCategoryLimitState(ThingCategoryDef cat, out PscDefLimit shared, out bool mixed, out int? sharedStackLimit)
        {
            shared = null;
            mixed = false;
            sharedStackLimit = null;
            var data = PscUiContext.Data;
            var filter = PscUiContext.Settings?.filter;
            var parentFilter = PscUiContext.Settings?.owner?.GetParentStoreSettings()?.filter;
            if (filter == null) return false;

            bool anyAllowed = false;
            bool anyNonDefault = false;
            int sharedLower = -1;
            int sharedUpper = -1;
            int stackLimit = -1;
            bool stackMixed = false;
            bool initialized = false;

            foreach (var d in cat.DescendantThingDefs)
            {
                if (d == null || !d.EverStorable(false)) continue;
                if (parentFilter != null && !parentFilter.Allows(d)) continue;
                if (!filter.Allows(d)) continue;

                anyAllowed = true;
                int dStack = Mathf.Max(1, d.stackLimit);
                if (stackLimit < 0) stackLimit = dStack;
                else if (stackLimit != dStack) stackMixed = true;

                var lim = data != null ? data.GetLimit(d) : null;
                int lower = lim != null ? lim.lowerRaw : -1;
                int upper = lim != null ? lim.upperRaw : -1;
                if (lower >= 0 || upper >= 0) anyNonDefault = true;

                if (!initialized)
                {
                    sharedLower = lower;
                    sharedUpper = upper;
                    initialized = true;
                }
                else if (sharedLower != lower || sharedUpper != upper)
                {
                    mixed = true;
                    break;
                }
            }

            if (!anyAllowed || !anyNonDefault) return false;
            if (!stackMixed && stackLimit > 0) sharedStackLimit = stackLimit;
            shared = new PscDefLimit { lowerRaw = sharedLower, upperRaw = sharedUpper };
            return true;
        }

        // Storable descendant defs of a category that the parent store settings permit. Shared by the
        // category menu and the vanilla-paint clear path.
        private static IEnumerable<ThingDef> StorableDescendants(ThingCategoryDef cat)
        {
            ThingFilter parentFilter = PscUiContext.Settings?.owner?.GetParentStoreSettings()?.filter;
            foreach (var d in cat.DescendantThingDefs)
            {
                if (d == null || !d.EverStorable(false)) continue;
                if (parentFilter != null && !parentFilter.Allows(d)) continue;
                yield return d;
            }
        }

        private static void OpenCategoryMenu(ThingCategoryDef cat)
        {
            var defs = new List<ThingDef>();
            foreach (var d in StorableDescendants(cat)) defs.Add(d);
            if (defs.Count == 0) return;
            Find.WindowStack.WindowOfType<PscItemLimitMenu>()?.Close(false);
            Find.WindowStack.Add(new PscItemLimitMenu(PscUiContext.Settings, PscUiContext.Unit, defs, cat.LabelCap));
        }
    }
}
