using UnityEngine;

namespace PrecisionStockpileControl
{
    // Single home for PSC's UI colors and widget geometry. Everything that was previously an inline
    // literal (slider sizes, row offsets, the muted greys) lives here so a visual tweak touches one
    // file and the same value can't drift between the editor, the row labels, and the window.
    internal static class PscUiTheme
    {
        // ---- Palette ------------------------------------------------------------------------------
        // Amber limit marker (I-beam, ticks at the rail ends) and the blue accent used for limit text.
        public static readonly Color LimitColor = new Color(1f, 0.82f, 0.16f);
        public static readonly Color LimitTextColor = new Color(0.45f, 0.8f, 1f);

        // Semi-transparent black drawn behind a row's limit label so it reads over the filter list.
        public static readonly Color LabelBackdrop = new Color(0f, 0f, 0f, 0.45f);

        // Muted greys. NoteText is the warm muted tone shared by the editor's mixed-stacks line and the
        // window's soft-cap note (previously 0.65 / 0.6 — unified). HintText/DisabledHintText are the
        // cooler greys for slider hints and disabled-state hints; TickMinor is the faint scale tick;
        // SliderRailFallback is the flat rail when the rail atlas texture is missing.
        public static readonly Color NoteText = new Color(0.75f, 0.75f, 0.62f);
        public static readonly Color HintText = new Color(0.72f, 0.72f, 0.72f);
        public static readonly Color DisabledHintText = new Color(0.7f, 0.7f, 0.7f);
        public static readonly Color TickMinor = new Color(0.5f, 0.5f, 0.5f);
        public static readonly Color SliderRailFallback = new Color(0.75f, 0.75f, 0.75f);

        // ---- Dual-slider geometry -----------------------------------------------------------------
        // The slider's "blank ends" buffer: values in [0, NullBuffer] read as unset (always / maximum).
        public const float NullBuffer = 0.06f;

        public const float SliderHandleHalf = 6f;       // handle is 2×half square, centred on the rail
        public const float SliderHandleSize = 12f;
        public const float SliderRailInset = 6f;        // rail is inset from the slider rect on each side
        public const float SliderRailHeight = 8f;
        public const float SliderRailHalfHeight = 4f;
        public const float SliderSideGap = 12f;         // gap between the numeric fields and the rail

        public const float TickEndHeight = 18f;         // the two bright "blank end" ticks
        public const float TickEndWidth = 3f;
        public const float TickFirstHeight = 14f;       // first concrete value just inside each buffer
        public const float TickFirstWidth = 2f;
        public const float TickMinorHeight = 9f;        // regular scale ticks
        public const float TickMinorWidth = 1f;
        public const float TickDefaultWidth = 2f;
        public const int TickCombThreshold = 12;

        // Fraction of a stack within which the slider "sticks" to a stack boundary (items mode).
        public const float StickyStackFraction = 0.1f;

        // ---- Limit-editor row layout --------------------------------------------------------------
        public const float EditorRowHeight = 100f;
        public const float FieldWidth = 72f;
        public const float EditorLabelHeight = 20f;
        public const float FieldHeight = 28f;
        public const float FieldTopOffset = 22f;        // numeric fields sit below the min/max labels
        public const float SliderTopOffset = 26f;
        public const float SliderHeight = 24f;
        public const float HintTopOffset = 54f;
        public const float HintHeight = 40f;

        // ---- Storage-filter row (per-item / category limit label + marker) ------------------------
        public const float RowCheckboxInset = 26f;      // checkbox sits ColumnWidth - inset from the left
        public const float RowLabelWidth = 128f;
        public const float RowLabelGap = 132f;          // label's left edge = checkbox.xMin - gap
        public const float RowLabelVContract = 2f;      // vertical inset of the label backdrop

        // ---- Settings window: left side-tab nav --------------------------------------------------
        // 2-pane settings layout: a fixed-width vertical text nav on the left, scrolling content on
        // the right. Window is fixed (900×700) so these are plain constants, not clamp ranges.
        public const float SettingsNavWidth = 160f;     // left nav column width
        public const float SettingsNavGap = 12f;        // gap between nav and content
        public const float SettingsNavRowHeight = 32f;  // one clickable tab row
        public const float SettingsNavRowGap = 2f;      // vertical gap between rows
        public const float SettingsNavLabelInset = 8f;  // label's left inset inside its row
        // Selected/hover row backings (faint white boxes); kept here so the alphas don't drift.
        public static readonly Color SettingsNavSelected = new Color(1f, 1f, 1f, 0.13f);
        public static readonly Color SettingsNavHover = new Color(1f, 1f, 1f, 0.06f);
        public static readonly Color SettingsNavDivider = new Color(1f, 1f, 1f, 0.18f);  // nav|content seam

        // ---- Settings window: bottom reset strip --------------------------------------------------
        // A fixed strip reserved below the scrolling content pane (every tab) for the "reset all" button,
        // so the button never scrolls away. Height includes a little breathing room above the button.
        public const float SettingsResetStripHeight = 42f;
        public const float SettingsResetButtonHeight = 30f;
        public const float SettingsResetButtonWidth = 200f;
    }
}
