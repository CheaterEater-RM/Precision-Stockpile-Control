using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Category rows summarize PSC state across currently allowed, storable descendants. Disallowed
    // descendants do not prevent a category from showing a shared range. A category whose allowed
    // descendants all belong to ONE limit group shows that group's label; right-click offers "Create
    // group from category".
    [HarmonyPatch(typeof(Listing_TreeThingFilter), "DoCategory")]
    public static class Listing_TreeThingFilter_DoCategory_Patch
    {
        public static void Prefix(Listing_TreeThingFilter __instance, TreeNode_ThingCategory node, out float __state)
        {
            __state = PscReflection.GetListingCurY(__instance);
            PscFilterPaint.ClearOwnedCheckbox();
            if (!PscUiContext.Active || node?.catDef == null) return;

            var e = Event.current;
            try
            {
                var catGroup = TryGetCategoryGroup(node.catDef);
                bool hasPscState = catGroup != null || TryGetCategoryLimitState(node.catDef, out _, out _);
                if (hasPscState)
                {
                    var checkRect = PscFilterRow.CheckboxRect(__instance, __state);
                    PscFilterPaint.OwnCheckbox(checkRect);
                    if (e != null && e.type == EventType.MouseDown && e.button == 0 && checkRect.Contains(e.mousePosition))
                    {
                        if (catGroup != null) OpenGroupEditor(PscUiContext.Settings, PscUiContext.Unit, catGroup); else OpenCategoryMenu(PscUiContext.Settings, PscUiContext.Unit, node.catDef);
                        e.Use();
                        return;
                    }
                }

                if (e == null || e.type != EventType.MouseDown || e.button != 1) return;
                if (PscFilterRow.RowRect(__instance, __state).Contains(e.mousePosition))
                {
                    OpenCategoryFloatMenu(node.catDef);
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
                    if (e.type == EventType.MouseDrag && PscFilterRow.RowRect(__instance, __state).Contains(e.mousePosition))
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

                var catGroup = TryGetCategoryGroup(node.catDef);
                bool hasPerDef = TryGetCategoryLimitState(node.catDef, out var shared, out bool mixed, out int? sharedStackLimit);
                if (catGroup == null && !hasPerDef) return;

                var iconRect = PscFilterRow.CheckboxRect(__instance, __state);

                // A vanilla left-drag allow/disallow paint over a category overwrites every descendant's
                // policy with the plain painted state (mirrors vanilla's category-paint cascade). The
                // category checkbox is suppressed while it has PSC state, so we apply it ourselves
                // (ClearLimit removes per-def limits AND group membership).
                if (PscFilterPaint.VanillaPaintActive && Mouse.IsOver(iconRect))
                {
                    bool allow = PscFilterPaint.VanillaPaintAllow;
                    foreach (var d in StorableDescendants(node.catDef))
                        PscEdit.ClearLimit(PscUiContext.Settings, d, allow);
                    PscFilterPaint.MarkVanillaPaintDirty(PscUiContext.Settings);
                    return;
                }

                float lh = ((Listing_Lines)__instance).lineHeight;
                PscUiWidgets.DrawLimitMarker(iconRect);

                if (catGroup != null)
                {
                    PscFilterRow.DrawLimitLabel(iconRect, __state, lh,
                        PscUiWidgets.CompactGroupLimit(catGroup), PscUiWidgets.FullGroupLimit(catGroup));
                }
                else if (!mixed)
                {
                    PscFilterRow.DrawLimitLabel(iconRect, __state, lh,
                        PscUiWidgets.CompactLimit(shared, sharedStackLimit),
                        PscUiWidgets.FullLimit(shared, sharedStackLimit));
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
                // A grouped descendant has no per-def limit; the group is summarized separately
                // (TryGetCategoryGroup). Skip it here so it neither counts as a per-def limit nor
                // forces "mixed".
                if (data != null && data.GroupOf(d) != null) continue;

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

        // Returns the single limit group shared by EVERY allowed storable descendant of `cat`, or null
        // if any allowed descendant is ungrouped or belongs to a different group. Lets a category row
        // show "A: 100-125" when the whole category is one group.
        private static PscLimitGroup TryGetCategoryGroup(ThingCategoryDef cat)
        {
            var data = PscUiContext.Data;
            var filter = PscUiContext.Settings?.filter;
            var parentFilter = PscUiContext.Settings?.owner?.GetParentStoreSettings()?.filter;
            if (data == null || filter == null) return null;

            PscLimitGroup group = null;
            bool any = false;
            foreach (var d in cat.DescendantThingDefs)
            {
                if (d == null || !d.EverStorable(false)) continue;
                if (parentFilter != null && !parentFilter.Allows(d)) continue;
                if (!filter.Allows(d)) continue;
                any = true;
                var g = data.GroupOf(d);
                if (g == null) return null;             // an ungrouped allowed descendant -> not single-group
                if (group == null) group = g;
                else if (group != g) return null;       // spans multiple groups
            }
            return any && group != null && !group.IsDefault ? group : null;
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

        // settings/unit passed in (not read from PscUiContext) — the float-menu callback runs after the
        // tab cleared PscUiContext, which would leave the menu operating on null settings.
        private static void OpenCategoryMenu(StorageSettings settings, PscHaulUnit unit, ThingCategoryDef cat)
        {
            var defs = new List<ThingDef>();
            foreach (var d in StorableDescendants(cat)) defs.Add(d);
            if (defs.Count == 0) return;
            Find.WindowStack.WindowOfType<PscItemLimitMenu>()?.Close(false);
            Find.WindowStack.Add(new PscItemLimitMenu(settings, unit, defs, cat.LabelCap));
        }

        // settings/unit passed in (captured at the call site), NOT read from PscUiContext here — a
        // right-click float-menu callback fires after the storage tab cleared PscUiContext, so reading it
        // at click time gives null settings and the editor self-closes on its first frame.
        private static void OpenGroupEditor(StorageSettings settings, PscHaulUnit unit, PscLimitGroup g)
        {
            Find.WindowStack.WindowOfType<PscGroupEditorWindow>()?.Close(false);
            Find.WindowStack.Add(new PscGroupEditorWindow(settings, unit, g));
        }

        // Right-click float menu for a category: edit per-def limits, edit the category's group (if it is
        // one), or create a new group from the category's currently-allowed descendants.
        private static void OpenCategoryFloatMenu(ThingCategoryDef cat)
        {
            var settings = PscUiContext.Settings;
            var unit = PscUiContext.Unit;
            var filter = settings?.filter;
            var allowed = new List<ThingDef>();
            foreach (var d in StorableDescendants(cat))
                if (filter == null || filter.Allows(d)) allowed.Add(d);

            var opts = new List<FloatMenuOption>();
            var catGroup = TryGetCategoryGroup(cat);
            if (catGroup != null)
                opts.Add(new FloatMenuOption("PSC_EditGroupLimit".Translate(catGroup.letter), () => OpenGroupEditor(settings, unit, catGroup)));
            opts.Add(new FloatMenuOption("PSC_EditCategoryLimits".Translate(), () => OpenCategoryMenu(settings, unit, cat)));
            if (allowed.Count >= 2)
                opts.Add(new FloatMenuOption("PSC_CreateGroupFromCategory".Translate(), () =>
                {
                    var g = PscEdit.CreateGroup(settings, unit, allowed, new PscDefLimit(), cat.LabelCap);
                    if (g != null) OpenGroupEditor(settings, unit, g);
                }));

            if (opts.Count > 0) Find.WindowStack.Add(new FloatMenu(opts));
        }
    }
}
