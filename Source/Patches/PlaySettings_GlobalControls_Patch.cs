using HarmonyLib;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Adds the storage-overlay toggle to the bottom-right play-settings control strip (map view
    // only), mirroring vanilla's overlay toggles. The toggle drives the session-only
    // PscOverlayState.Active. This one-bool postfix is the overlay's only always-on cost.
    [HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
    public static class PlaySettings_GlobalControls_Patch
    {
        public static void Postfix(WidgetRow row, bool worldView)
        {
            if (worldView || row == null) return;
            bool v = PscOverlayState.Active;
            row.ToggleableIcon(ref v, PscOverlayState.ButtonTex,
                "PSC_OverlayToggleTip".Translate(), SoundDefOf.Mouseover_ButtonToggle);
            PscOverlayState.Active = v;
        }
    }
}
