using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Row geometry + limit-label drawing shared by the DoThingDef (per-item) and DoCategory
    // (category summary) storage-filter patches. Both rows place the checkbox at the right edge and,
    // when a limit is present, draw a right-aligned blue limit label in the strip to its left.
    internal static class PscFilterRow
    {
        // The vanilla checkbox slot for a filter row (right-aligned, square at the line height).
        public static Rect CheckboxRect(Listing_TreeThingFilter inst, float rowY)
        {
            float lh = ((Listing_Lines)inst).lineHeight;
            return new Rect(inst.ColumnWidth - PscUiTheme.RowCheckboxInset, rowY, lh, lh);
        }

        // The full-width row rect (used for right-click hit-testing).
        public static Rect RowRect(Listing_TreeThingFilter inst, float rowY)
            => new Rect(0f, rowY, inst.ColumnWidth, ((Listing_Lines)inst).lineHeight);

        // Dark backdrop + right-aligned blue limit text + tooltip in the strip left of the marker.
        // The caller draws the marker itself (PscUiWidgets.DrawLimitMarker), since DoCategory draws the
        // marker even when the label is suppressed (mixed range).
        public static void DrawLimitLabel(Rect iconRect, float rowY, float lineHeight, string compact, string tooltip)
        {
            var prevFont = Text.Font;
            var prevAnchor = Text.Anchor;
            var prevColor = GUI.color;
            try
            {
                // Right edge is fixed (just left of the checkbox); the backdrop and text hug the
                // actual text width, capped at RowLabelWidth and growing leftward only as needed, so
                // a short limit no longer paints a full-width bar over the item name.
                Text.Font = GameFont.Tiny;
                float xMax = iconRect.xMin - PscUiTheme.RowLabelGap + PscUiTheme.RowLabelWidth;
                float w = Mathf.Min(PscUiTheme.RowLabelWidth, Text.CalcSize(compact).x + 6f);
                var labelRect = new Rect(xMax - w, rowY, w, lineHeight);

                GUI.color = PscUiTheme.LabelBackdrop;
                GUI.DrawTexture(labelRect.ContractedBy(0f, PscUiTheme.RowLabelVContract), BaseContent.WhiteTex);
                Text.Anchor = TextAnchor.MiddleRight;
                GUI.color = PscUiTheme.LimitTextColor;
                Widgets.Label(labelRect, compact.Truncate(labelRect.width));
                TooltipHandler.TipRegion(labelRect, tooltip);
            }
            finally
            {
                Text.Font = prevFont;
                Text.Anchor = prevAnchor;
                GUI.color = prevColor;
            }
        }
    }
}
