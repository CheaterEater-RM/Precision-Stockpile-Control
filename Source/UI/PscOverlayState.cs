using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Session-only view state for the on-map storage overlay. The bottom-right play-settings toggle
    // (PlaySettings_GlobalControls_Patch) drives Active; PscStorageOverlay (panels) and
    // PscFeederOverlay (draw-all routes) read it. This is a view preference like vanilla's
    // play-settings overlays: NOT scribed and NOT reset on load — it persists for the app session and
    // defaults off at launch (so it has no save-contract impact). Replaces PscFeederOverlay.ShowAll.
    //
    // Also owns the toggle-button texture and the priority-band colour/initial mapping the panels use.
    [StaticConstructorOnStartup]
    public static class PscOverlayState
    {
        public static bool Active;

        // PSC art (exists on disk); BadTex on a missing file is a visible "art problem" signal rather
        // than a silent vanilla stand-in.
        public static readonly Texture2D ButtonTex =
            ContentFinder<Texture2D>.Get("UI/Overlay/StorageOverlay", reportFailure: false)
            ?? BaseContent.BadTex;

        private static readonly Color LowColor       = new Color(0.62f, 0.62f, 0.62f);
        private static readonly Color NormalColor    = new Color(0.90f, 0.90f, 0.88f);
        private static readonly Color PreferredColor = new Color(0.50f, 0.72f, 1.00f);
        private static readonly Color ImportantColor = new Color(1.00f, 0.85f, 0.30f);
        private static readonly Color CriticalColor  = new Color(0.95f, 0.40f, 0.35f);
        private static readonly Color UnstoredColor  = new Color(0.45f, 0.45f, 0.45f);

        public static Color BandColor(StoragePriority p)
        {
            switch (p)
            {
                case StoragePriority.Critical:  return CriticalColor;
                case StoragePriority.Important: return ImportantColor;
                case StoragePriority.Preferred: return PreferredColor;
                case StoragePriority.Normal:    return NormalColor;
                case StoragePriority.Low:       return LowColor;
                default:                        return UnstoredColor;
            }
        }

        // Cached single-char band initials (no per-frame string alloc).
        public static string BandInitial(StoragePriority p)
        {
            switch (p)
            {
                case StoragePriority.Critical:  return "C";
                case StoragePriority.Important: return "I";
                case StoragePriority.Preferred: return "P";
                case StoragePriority.Normal:    return "N";
                case StoragePriority.Low:       return "L";
                default:                        return "–";
            }
        }
    }
}
