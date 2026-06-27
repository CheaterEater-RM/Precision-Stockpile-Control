using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
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
        // settings/unit passed in (not read from PscUiContext) for the same reason as OpenGroupEditor:
        // the float-menu callback runs after the tab cleared PscUiContext, which would leave the menu
        // operating on null settings.
        private static void OpenItemMenu(StorageSettings settings, PscHaulUnit unit, ThingDef tDef)
        {
            Find.WindowStack.WindowOfType<PscItemLimitMenu>()?.Close(false);
            Find.WindowStack.Add(new PscItemLimitMenu(settings, unit, new List<ThingDef> { tDef }, tDef.LabelCap));
        }

        // settings/unit are passed in (captured at the call site), NOT read from PscUiContext here: a
        // right-click float-menu callback fires AFTER the storage tab's Finalizer has cleared
        // PscUiContext, so reading it at click time yields null settings and the editor self-closes on its
        // first frame (the validity guard). The left-click caller runs during the tab draw, but passes the
        // (still-valid) context values too for one consistent path.
        private static void OpenGroupEditor(StorageSettings settings, PscHaulUnit unit, PscLimitGroup g)
        {
            Find.WindowStack.WindowOfType<PscGroupEditorWindow>()?.Close(false);
            Find.WindowStack.Add(new PscGroupEditorWindow(settings, unit, g));
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
                opts.Add(new FloatMenuOption("PSC_EditGroupLimit".Translate(group.letter), () => OpenGroupEditor(settings, unit, group)));
            else
                opts.Add(new FloatMenuOption("PSC_EditItemLimit".Translate(), () => OpenItemMenu(settings, unit, tDef)));

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

            // Ad-hoc creation: start a new group seeded with just this item, then open the editor to grow
            // it / set its shared limit. Only for an ungrouped def (a grouped one uses Add-to / Remove).
            if (group == null)
                opts.Add(new FloatMenuOption("PSC_NewGroup".Translate(), () => NewGroupFromDef(settings, unit, data, tDef)));

            if (group != null)
                opts.Add(new FloatMenuOption("PSC_RemoveFromGroup".Translate(),
                    () => PscEdit.RemoveFromGroup(settings, tDef)));

            if (opts.Count > 0) Find.WindowStack.Add(new FloatMenu(opts));
        }

        // Create a single-item group from `tDef`. If the def already carries a per-def cap, seed the new
        // group's shared limit from it in Items mode (preserve the exact number — CreateGroup copies it
        // before NormalizeGroups strips the per-def entry); otherwise a fresh stacks-mode draft.
        private static void NewGroupFromDef(StorageSettings settings, PscHaulUnit unit, PscStorageData data, ThingDef tDef)
        {
            bool hasPerDef = data != null && data.HasLimit(tDef);
            var seed = hasPerDef ? data.GetLimit(tDef) : new PscDefLimit();
            var mode = hasPerDef ? PscGroupCountMode.Items : PscGroupCountMode.Stacks;
            var g = PscEdit.CreateGroup(settings, unit, new List<ThingDef> { tDef }, seed, mode: mode);
            if (g != null) OpenGroupEditor(settings, unit, g);
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
                    if (group != null) { OpenGroupEditor(PscUiContext.Settings, PscUiContext.Unit, group); e.Use(); return; }
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
