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

                // Blacklisted storage (bookcases, graves, etc.): leave __state.settings null so the
                // postfix draws nothing, and skip PscUiContext.Set so per-row right-click limits are
                // suppressed too. PSC then behaves exactly like vanilla on these units.
                if (PscStorageButtonFilter.ShouldHide(parent)) return;

                __state.settings = settings;
                __state.search = PscReflection.GetQuickSearchFilter(__instance);

                // Always set the context (even with no PSC data yet) so right-click on a row can
                // create a first limit. The per-row label still only draws when a limit exists.
                PscUiContext.Set(settings, PscHaulUnit.ResolveSettings(settings));
            }
            catch
            {
                // UI reflection is best-effort; never let it break the storage tab.
            }
        }

        public static void Postfix(State __state)
        {
            if (__state.settings == null) return;

            // Fine-order controls (letter / 1-10 level) beside the vanilla Priority button.
            PscPriorityBox.Draw(__state.settings);

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

        public static void Finalizer()
        {
            PscFilterPaint.FlushPendingVanillaPaint();
            PscFilterPaint.Reset();
            PscUiContext.Clear();
        }
    }
}
