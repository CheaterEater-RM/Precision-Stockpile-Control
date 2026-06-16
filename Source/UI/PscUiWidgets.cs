using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    [StaticConstructorOnStartup]
    internal static class PscUiWidgets
    {
        public const float StorageTabReserveHeight = 30f;

        // PSC entry button placement on the storage tab, in the FillTab BeginGroup-contracted space.
        // ThingFilterUI reserves StorageTabReserveHeight at the top of the filter list (see
        // ThingFilterUI_Patch); the button sits just above that reserved strip. Kept here as the single
        // source of truth so the reserve and the button can't drift apart.
        private const float EntryButtonX = 10f;
        private const float EntryButtonY = 45f;
        private const float EntryButtonWidth = 196f;
        private const float EntryButtonHeight = 24f;

        public static Rect EntryButtonRect()
            => new Rect(EntryButtonX, EntryButtonY, EntryButtonWidth, EntryButtonHeight);

        private static readonly Texture2D LimitI =
            ContentFinder<Texture2D>.Get("UI/Widgets/PSC_LimitI", false);
        private static readonly Texture2D SliderRail =
            ContentFinder<Texture2D>.Get("UI/Buttons/SliderRail", false);
        private static readonly Texture2D SliderHandle =
            ContentFinder<Texture2D>.Get("UI/Buttons/SliderHandle", false);

        public static void DrawLimitI(Rect rect)
        {
            var prev = GUI.color;
            GUI.color = PscUiTheme.LimitColor;
            if (LimitI != null)
            {
                GUI.DrawTexture(rect, LimitI, ScaleMode.ScaleToFit);
            }
            else
            {
                DrawIBeamFallback(rect);
            }
            GUI.color = prev;
        }

        public static void DrawLimitMarker(Rect rect)
        {
            var prev = GUI.color;
            GUI.color = PscUiTheme.LimitColor;
            DrawIBeamFallback(rect.ContractedBy(3f));
            GUI.color = prev;
        }

        public static void DrawSliderRail(Rect rect)
        {
            if (SliderRail != null) Widgets.DrawAtlas(rect, SliderRail);
            else
            {
                var prev = GUI.color;
                GUI.color = PscUiTheme.SliderRailFallback;
                GUI.DrawTexture(rect, BaseContent.WhiteTex);
                GUI.color = prev;
            }
        }

        public static void DrawSliderHandle(Rect rect)
        {
            if (SliderHandle != null) GUI.DrawTexture(rect, SliderHandle);
            else DrawLimitI(rect);
        }

        public static string CompactLimit(PscDefLimit lim)
        {
            string lo = lim != null && lim.Lower.HasValue ? lim.Lower.Value.ToString() : "";
            string hi = lim != null && lim.Upper.HasValue ? lim.Upper.Value.ToString() : "";
            return lo + "-" + hi;
        }

        public static string CompactLimit(PscDefLimit lim, ThingDef def)
        {
            return CompactLimit(lim, def == null ? (int?)null : Mathf.Max(1, def.stackLimit));
        }

        public static string CompactLimit(PscDefLimit lim, int? stackLimit)
        {
            if (!stackLimit.HasValue) return CompactLimit(lim);
            string lo = lim != null && lim.Lower.HasValue ? FormatItemsStacks(lim.Lower.Value, stackLimit.Value) : "";
            string hi = lim != null && lim.Upper.HasValue ? FormatItemsStacks(lim.Upper.Value, stackLimit.Value) : "";
            return lo + "-" + hi;
        }

        public static string FullLimit(PscDefLimit lim, ThingDef def)
        {
            if (lim == null) return "PSC_Always".Translate() + " - " + "PSC_Maximum".Translate();
            return FormatSide(lim.Lower, def, true) + " - " + FormatSide(lim.Upper, def, false);
        }

        public static string FullLimit(PscDefLimit lim, int? stackLimit)
        {
            if (!stackLimit.HasValue) return FullLimit(lim, (ThingDef)null);
            if (lim == null) return "PSC_Always".Translate() + " - " + "PSC_Maximum".Translate();
            return FormatSide(lim.Lower, stackLimit.Value, true) + " - " + FormatSide(lim.Upper, stackLimit.Value, false);
        }

        public static string FormatItemsStacks(int value, ThingDef def)
        {
            return def == null ? value.ToString() : FormatItemsStacks(value, Mathf.Max(1, def.stackLimit));
        }

        public static string FormatItemsStacks(int value, int stackLimit)
        {
            int stack = Mathf.Max(1, stackLimit);
            int stacks = value <= 0 ? 0 : Mathf.CeilToInt(value / (float)stack);
            return value + " (" + stacks + ")";
        }

        private static string FormatSide(int? value, ThingDef def, bool lower)
        {
            if (!value.HasValue) return lower ? "PSC_Always".Translate().ToString() : "PSC_Maximum".Translate().ToString();
            return FormatItemsStacks(value.Value, def);
        }

        private static string FormatSide(int? value, int stackLimit, bool lower)
        {
            if (!value.HasValue) return lower ? "PSC_Always".Translate().ToString() : "PSC_Maximum".Translate().ToString();
            return FormatItemsStacks(value.Value, stackLimit);
        }

        private static void DrawIBeamFallback(Rect box)
        {
            float cx = box.center.x;
            float top = box.yMin + 2f;
            float bot = box.yMax - 2f;
            float t = Mathf.Max(2f, box.height / 7f);
            float halfW = box.width * 0.42f;
            GUI.DrawTexture(new Rect(cx - halfW, top, halfW * 2f, t), BaseContent.WhiteTex);
            GUI.DrawTexture(new Rect(cx - halfW, bot - t, halfW * 2f, t), BaseContent.WhiteTex);
            GUI.DrawTexture(new Rect(cx - t / 2f, top, t, bot - top), BaseContent.WhiteTex);
        }
    }
}
