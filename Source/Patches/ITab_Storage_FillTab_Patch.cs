using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Depth-scaling entry point (design §10.1). One PSC button on the stockpile tab; everything
    // else lives behind it. The prefix also stashes the per-row UI context (so DoThingDef can draw
    // the read-only limit label); the finalizer clears it even if FillTab throws.
    //
    // The vanilla FillTab draws inside a BeginGroup contracted by 10 and EndGroups before our
    // postfix runs. ThingFilterUI_Patch reserves a strip under the priority button; this postfix
    // draws the PSC entry button into that strip in window-space coordinates.
    [HarmonyPatch(typeof(ITab_Storage), "FillTab")]
    public static class ITab_Storage_FillTab_Patch
    {
        public struct State
        {
            public StorageSettings settings;
            public QuickSearchFilter search;
            public bool priorityOnly;
        }

        public static void Prefix(ITab_Storage __instance, out State __state)
        {
            __state = default;
            try
            {
                var parent = PscReflection.GetSelStoreSettingsParent(__instance);
                if (parent == null) return;
                var settings = parent.GetStoreSettings();
                if (settings == null) return;

                // Limit-hidden storage (graves, feedstock vats, shell holders): a count limit is
                // meaningless here, so no limits context (no per-row limit UI, entry button, or toggle
                // strip). It DOES still get the fine-order priority box, which self-gates on vanilla's
                // priority visibility (only graves have a band). Mark the priority-only path and set
                // just the priority context, then let the postfix draw the level box alone.
                if (PscStorageButtonFilter.ShouldHideLimits(parent))
                {
                    __state.settings = settings;
                    __state.priorityOnly = true;
                    PscUiContext.SetPriorityOnly(settings);
                    return;
                }

                __state.settings = settings;
                __state.search = PscReflection.GetQuickSearchFilter(__instance);

                // Always set the context (even with no PSC data yet) so right-click on a row can
                // create a first limit. The per-row label still only draws when a limit exists.
                // For an unbuilt storage (blueprint / frame) the unit is invalid; stash the built
                // shelf's stack capacity from its BuildDef so the limit sliders show the same ceiling
                // they will once built (PscLimitEditor falls back to it).
                var unit = PscHaulUnit.ResolveSettings(settings);
                PscUiContext.Set(settings, unit, unit.IsValid ? 0 : PreviewStackSlots(parent));
            }
            catch (System.Exception e)
            {
                // UI reflection is best-effort; never let it break the storage tab. Surface it once
                // (keyed, so it can't spam this per-frame path) so future RimWorld UI drift is visible.
                Log.WarningOnce("[PSC] storage-tab setup failed (RimWorld UI may have changed): " + e,
                    0x12C5_F11B);
            }
        }

        public static void Postfix(State __state)
        {
            if (__state.settings == null) return;

            // Limit-hidden storage (graves): draw only the fine-order level box (no letter box, no
            // entry button / toggle strip / window retarget), then stop.
            if (__state.priorityOnly)
            {
                PscPriorityBox.Draw(__state.settings, priorityOnly: true);
                return;
            }

            // Fine-order controls (letter / 1-10 level) beside the vanilla Priority button.
            PscPriorityBox.Draw(__state.settings, priorityOnly: false);

            var unit = PscHaulUnit.ResolveSettings(__state.settings);

            // Keep an open PSC window synced to the current selection: when the player picks a
            // different stockpile (the storage tab is sticky, so FillTab runs for the new one),
            // retarget the existing window rather than leaving it on the old stockpile.
            var open = Find.WindowStack.WindowOfType<PscControlWindow>();
            if (open != null && !ReferenceEquals(open.Settings, __state.settings))
                open.Retarget(__state.settings, unit, __state.search);

            var rect = PscUiWidgets.EntryButtonRect();
            TooltipHandler.TipRegion(rect, "PSC_ButtonTip".Translate());
            if (Widgets.ButtonText(rect, "PSC_ButtonLabel".Translate()))
            {
                // Toggle: if a PSC window is already open, the button closes it; otherwise open a
                // fresh one for the currently selected storage.
                if (open != null)
                    open.Close(false);
                else
                    Find.WindowStack.Add(new PscControlWindow(__state.settings, unit, __state.search));
            }

            // Quick-toggle strip (batch in/out, only-from/to-routes, alarm) right of the button.
            PscToggleStrip.Draw(__state.settings, unit, __state.search);
        }

        // Stack capacity the built shelf WILL have, for the limit-slider ceiling on an unbuilt
        // (blueprint / frame) storage whose live unit does not exist yet. Mirrors vanilla's own
        // capacity formula (Building_Storage.SpaceRemainingFor: maxItemsInCell * Size.Area). A
        // blueprint/frame Thing's def.entityDefToBuild is the real building def; a built storage's is
        // null, so this returns 0 there and the live-unit capacity is used instead.
        private static int PreviewStackSlots(IStoreSettingsParent parent)
        {
            if (parent is Thing t && t.def?.entityDefToBuild is ThingDef built)
            {
                int perCell = built.building != null ? built.building.maxItemsInCell : 1;
                return Mathf.Max(1, perCell) * Mathf.Max(1, built.Size.Area);
            }
            return 0;
        }

        public static void Finalizer()
        {
            PscFilterPaint.FlushPendingVanillaPaint();
            PscFilterPaint.Reset();
            PscUiContext.Clear();
        }
    }
}
