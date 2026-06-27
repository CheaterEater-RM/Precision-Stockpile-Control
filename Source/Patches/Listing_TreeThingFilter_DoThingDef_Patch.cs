using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Depth-scaling per-item UI. Limited / grouped rows own the vanilla checkbox slot: left-click opens
    // the editor (per-item menu, or the group editor for a grouped def) and left-drag propagates a
    // per-def limit. Right-click opens a float menu (edit limit / add to group / remove from group).
    // Untouched rows remain pure vanilla.
    [HarmonyPatch(typeof(Listing_TreeThingFilter), "DoThingDef")]
    public static class Listing_TreeThingFilter_DoThingDef_Patch
    {
        private static void OpenItemMenu(ThingDef tDef)
        {
            Find.WindowStack.WindowOfType<PscItemLimitMenu>()?.Close(false);
            Find.WindowStack.Add(new PscItemLimitMenu(PscUiContext.Settings, PscUiContext.Unit,
                new List<ThingDef> { tDef }, tDef.LabelCap));
        }

        private static void OpenGroupEditor(PscLimitGroup g)
        {
            Find.WindowStack.WindowOfType<PscGroupEditorWindow>()?.Close(false);
            Find.WindowStack.Add(new PscGroupEditorWindow(PscUiContext.Settings, PscUiContext.Unit, g));
        }

        // Right-click float menu for a single item row: edit its limit (per-item menu, or the group
        // editor when grouped), add it to an existing group, or remove it from its group.
        private static void OpenRowFloatMenu(ThingDef tDef)
        {
            var data = PscUiContext.Data;
            var settings = PscUiContext.Settings;
            var unit = PscUiContext.Unit;
            var group = data?.GroupOf(tDef);
            var opts = new List<FloatMenuOption>();

            if (group != null)
                opts.Add(new FloatMenuOption("PSC_EditGroupLimit".Translate(group.letter), () => OpenGroupEditor(group)));
            else
                opts.Add(new FloatMenuOption("PSC_EditItemLimit".Translate(), () => OpenItemMenu(tDef)));

            if (data?.limitGroups != null)
            {
                for (int i = 0; i < data.limitGroups.Count; i++)
                {
                    var g = data.limitGroups[i];
                    if (g == null || g == group) continue;
                    opts.Add(new FloatMenuOption("PSC_AddToGroupX".Translate(g.letter),
                        () => PscEdit.AddToGroup(settings, unit, g, tDef)));
                }
            }

            if (group != null)
                opts.Add(new FloatMenuOption("PSC_RemoveFromGroup".Translate(),
                    () => PscEdit.RemoveFromGroup(settings, tDef)));

            if (opts.Count > 0) Find.WindowStack.Add(new FloatMenu(opts));
        }

        public static void Prefix(Listing_TreeThingFilter __instance, ThingDef tDef, out float __state)
        {
            __state = PscReflection.GetListingCurY(__instance);
            PscFilterPaint.ClearOwnedCheckbox();
            if (!PscUiContext.Active || tDef == null) return;
            var e = Event.current;
            try
            {
                var data = PscUiContext.Data;
                var group = data?.GroupOf(tDef);
                var limit = data?.GetLimit(tDef);
                bool hasPerDef = limit != null && !limit.IsDefault;
                bool owns = hasPerDef || group != null;       // owns the checkbox slot (label + clicks)
                var checkRect = PscFilterRow.CheckboxRect(__instance, __state);

                if (owns) PscFilterPaint.OwnCheckbox(checkRect);

                if (e == null || e.type != EventType.MouseDown) return;

                if (e.button == 0 && checkRect.Contains(e.mousePosition))
                {
                    if (group != null) { OpenGroupEditor(group); e.Use(); return; }
                    if (hasPerDef)
                    {
                        PscFilterPaint.Begin(0, PscUiContext.Settings, PscUiContext.Unit, tDef, limit, e.mousePosition);
                        e.Use();
                        return;
                    }
                }

                // Right-click opens the per-row float menu directly on MouseDown (mirrors DoCategory).
                if (e.button == 1 && PscFilterRow.RowRect(__instance, __state).Contains(e.mousePosition))
                {
                    OpenRowFloatMenu(tDef);
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
                if (data == null) return;
                var group = data.GroupOf(tDef);
                bool hasPerDef = data.HasLimit(tDef);
                if (!hasPerDef && group == null) return;

                var iconRect = PscFilterRow.CheckboxRect(__instance, __state);

                // A vanilla left-drag allow/disallow paint passing over a limited / grouped row overwrites
                // the precise policy with the plain painted state. Such rows keep the vanilla checkbox
                // suppressed, so we replicate the paint via ClearLimit (removes per-def limit OR group
                // membership, then SetAllow).
                if (PscFilterPaint.VanillaPaintActive && Mouse.IsOver(iconRect))
                {
                    PscEdit.ClearLimit(PscUiContext.Settings, tDef, PscFilterPaint.VanillaPaintAllow);
                    PscFilterPaint.MarkVanillaPaintDirty(PscUiContext.Settings);
                    return; // policy cleared; the row reverts to a normal vanilla checkbox next frame
                }

                float lh = ((Listing_Lines)__instance).lineHeight;
                if (group != null)
                {
                    PscFilterRow.DrawLimitLabel(iconRect, __state, lh,
                        PscUiWidgets.CompactGroupLimit(group), PscUiWidgets.FullGroupLimit(group));
                }
                else
                {
                    var lim = data.GetLimit(tDef);
                    PscFilterRow.DrawLimitLabel(iconRect, __state, lh,
                        PscUiWidgets.CompactLimit(lim, tDef), PscUiWidgets.FullLimit(lim, tDef));
                }
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
