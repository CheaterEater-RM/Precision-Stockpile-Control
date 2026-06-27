using System.Text;
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
        // Narrowed (was 196) so the quick-toggle strip (PscToggleStrip) fits to its right on the same
        // row. Wide enough for the "Stockpile Control" label at GameFont.Small; if a translation
        // clips, shorten PSC_ButtonLabel rather than widening back into the strip.
        private const float EntryButtonWidth = 140f;
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

        // A faint "?" help glyph drawn beside a label; the caller registers the tooltip over the
        // same rect. Kept here so the glyph style is consistent wherever in-window help appears.
        public static void DrawHelpIcon(Rect rect)
        {
            var prevColor = GUI.color;
            var prevFont = Text.Font;
            var prevAnchor = Text.Anchor;
            GUI.color = PscUiTheme.HintText;
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(rect, "?");
            Text.Anchor = prevAnchor;
            Text.Font = prevFont;
            GUI.color = prevColor;
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

        // Compact group row label: "A: 100-125" (items) or "A: 6-8 stacks" (stacks mode) — the letter plus
        // the shared limit in the group's count unit. Per-def stack-count parens are never shown for a
        // group (the value already IS the chosen unit; in stacks mode the suffix names it).
        public static string CompactGroupLimit(PscLimitGroup g)
        {
            if (g == null) return "";
            string lo = g.limit != null && g.limit.Lower.HasValue ? g.limit.Lower.Value.ToString() : "";
            string hi = g.limit != null && g.limit.Upper.HasValue ? g.limit.Upper.Value.ToString() : "";
            string prefix = string.IsNullOrEmpty(g.letter) ? "" : g.letter + ": ";
            string suffix = g.countMode == PscGroupCountMode.Stacks ? " " + "PSC_ModeStacks".Translate() : "";
            return prefix + lo + "-" + hi + suffix;
        }

        // Format one group limit value in the group's unit ("6 stacks" / "100"); unitless in items mode so
        // the Always/Maximum words and the items default stay concise.
        private static string GroupVal(PscLimitGroup g, int v)
            => g.countMode == PscGroupCountMode.Stacks ? v + " " + "PSC_ModeStacks".Translate() : v.ToString();

        // Full group tooltip: titles the group (letter + optional name), states it is a combined total
        // across N items, then lists the members so "what is A?" resolves on hover.
        public static string FullGroupLimit(PscLimitGroup g)
        {
            if (g == null) return "";
            string lo = g.limit != null && g.limit.Lower.HasValue ? GroupVal(g, g.limit.Lower.Value)
                : "PSC_Always".Translate().ToString();
            string hi = g.limit != null && g.limit.Upper.HasValue ? GroupVal(g, g.limit.Upper.Value)
                : "PSC_Maximum".Translate().ToString();
            var sb = new StringBuilder();
            sb.Append(string.IsNullOrEmpty(g.name)
                ? (string)"PSC_GroupTitleLetter".Translate(g.letter)
                : (string)"PSC_GroupTitleNamed".Translate(g.letter, g.name));
            sb.Append("\n");
            sb.Append("PSC_GroupCombinedRange".Translate(lo, hi, g.members.Count));
            if (g.members.Count > 0)
            {
                sb.Append("\n");
                sb.Append("PSC_GroupMembers".Translate());
                sb.Append(" ");
                for (int i = 0; i < g.members.Count; i++)
                {
                    if (g.members[i] == null) continue;
                    if (i > 0) sb.Append(", ");
                    sb.Append(g.members[i].LabelCap);
                }
            }
            return sb.ToString();
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
