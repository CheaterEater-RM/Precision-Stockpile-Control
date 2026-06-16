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
                var parent = Traverse.Create(__instance).Property("SelStoreSettingsParent")
                    .GetValue<IStoreSettingsParent>();
                if (parent == null) return;
                var settings = parent.GetStoreSettings();
                if (settings == null) return;
                __state.settings = settings;
                __state.search = Traverse.Create(__instance).Field("thingFilterState")
                    .Field("quickSearch").Field("filter").GetValue<QuickSearchFilter>();

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

            var rect = PscUiWidgets.EntryButtonRect();
            if (Widgets.ButtonText(rect, "PSC_ButtonLabel".Translate()))
            {
                // Always (re)open for the currently selected storage, replacing any prior window.
                Find.WindowStack.WindowOfType<PscControlWindow>()?.Close(false);
                var unit = PscHaulUnit.ResolveSettings(__state.settings);
                Find.WindowStack.Add(new PscControlWindow(__state.settings, unit, __state.search));
            }
        }

        public static void Finalizer()
        {
            PscFilterPaint.FlushPendingVanillaPaint();
            PscFilterPaint.Reset();
            PscUiContext.Clear();
        }
    }
}
