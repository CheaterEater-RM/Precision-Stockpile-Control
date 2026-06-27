using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    public class PscLimitEditorTarget
    {
        public ThingDef exactDef;
        public int? sharedStackLimit;
        public int? firstStackLimit;
        public int maxStackLimit = 1;   // largest member stackLimit (for the pooled-items slider ceiling)
        public bool mixedStackLimits;

        public bool HasStackContext => exactDef != null || sharedStackLimit.HasValue || firstStackLimit.HasValue;
        public bool ItemsModeAllowed => !mixedStackLimits;
        public int StackLimit => exactDef != null ? Mathf.Max(1, exactDef.stackLimit) : Mathf.Max(1, sharedStackLimit ?? firstStackLimit ?? 1);

        public static PscLimitEditorTarget FromDefs(System.Collections.Generic.IEnumerable<ThingDef> defs)
        {
            var ctx = new PscLimitEditorTarget();
            int count = 0;
            int shared = -1;
            foreach (var d in defs)
            {
                if (d == null) continue;
                count++;
                int stack = Mathf.Max(1, d.stackLimit);
                if (!ctx.firstStackLimit.HasValue) ctx.firstStackLimit = stack;
                if (stack > ctx.maxStackLimit) ctx.maxStackLimit = stack;
                if (shared < 0) shared = stack;
                else if (shared != stack) ctx.mixedStackLimits = true;
                if (count == 1) ctx.exactDef = d;
                else ctx.exactDef = null;
            }
            if (!ctx.mixedStackLimits && shared > 0) ctx.sharedStackLimit = shared;
            return ctx;
        }
    }

    // Shared working-state widget for editing one (lower?, upper?) limit pair. Limits are stored
    // as items; stacks mode is converted per ThingDef at apply time using the def's live stackLimit.
    // The dual-handle slider itself lives in PscLimitSlider; this class owns the values, the numeric
    // fields, and the items/stacks conversions.
    public class PscLimitEditor
    {
        private const int GenericItemMax = 5000;

        public bool stacksMode = true;
        // Pooled mode (limit groups): the limit is a single RAW item total spanning several defs, so
        // there is no per-def stack conversion and no items/stacks toggle — always items.
        public bool pooled;
        public int? lowerVal;
        public int? upperVal;
        public string lowerBuf = "";
        public string upperBuf = "";

        public void Draw(Listing_Standard list, PscHaulUnit unit, PscLimitEditorTarget target = null)
        {
            target ??= new PscLimitEditorTarget();
            if (pooled)
            {
                // Group editor: a combined total in stacks or items. The toggle is ALWAYS available — even
                // for mixed stack sizes, where switching to items just reinterprets the number (no single
                // conversion factor exists). Uniform-stack members convert cleanly via the shared stackLimit.
                Text.Font = GameFont.Tiny;
                GUI.color = PscUiTheme.NoteText;
                list.Label("PSC_GroupCombinedNote".Translate());
                if (target.mixedStackLimits && stacksMode)
                    list.Label("PSC_GroupMixedStacksNote".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;

                Rect pmRect = list.GetRect(Text.LineHeight);
                Widgets.Label(pmRect.LeftHalf().Rounded(), "PSC_Mode".Translate());
                if (Widgets.ButtonText(pmRect.RightHalf().Rounded(),
                        (stacksMode ? "PSC_ModeStacks" : "PSC_ModeItems").Translate()))
                    ToggleMode(target);
                TooltipHandler.TipRegion(pmRect.RightHalf().Rounded(), "PSC_CountByTip".Translate());
            }
            else
            {
                if (!target.ItemsModeAllowed && !stacksMode)
                {
                    ConvertItemsToStacks(target);
                    stacksMode = true;
                }

                Rect modeRect = list.GetRect(Text.LineHeight);
                Rect modeLabel = modeRect.LeftHalf().Rounded();
                Rect modeButton = modeRect.RightHalf().Rounded();
                Widgets.Label(modeLabel, "PSC_Mode".Translate());
                bool prevEnabled = GUI.enabled;
                GUI.enabled = prevEnabled && target.ItemsModeAllowed;
                if (Widgets.ButtonText(modeButton, (stacksMode ? "PSC_ModeStacks" : "PSC_ModeItems").Translate()))
                {
                    ToggleMode(target);
                }
                GUI.enabled = prevEnabled;
                if (target.ItemsModeAllowed)
                    TooltipHandler.TipRegion(modeButton, "PSC_CountByTip".Translate());
                if (!target.ItemsModeAllowed)
                {
                    TooltipHandler.TipRegion(modeButton, "PSC_ItemsModeMixedStacksTip".Translate());
                    Text.Font = GameFont.Tiny;
                    GUI.color = PscUiTheme.NoteText;
                    list.Label("PSC_ItemsModeMixedStacksTip".Translate());
                    GUI.color = Color.white;
                    Text.Font = GameFont.Small;
                }
            }

            int sliderMax = SliderMax(unit, target);
            ClampValues(sliderMax);

            list.Gap(6f);
            Rect row = list.GetRect(PscUiTheme.EditorRowHeight);
            DrawEditorRow(row, sliderMax, target);
            list.Gap(2f);
        }

        public string PreviewString(PscLimitEditorTarget target = null)
        {
            target ??= new PscLimitEditorTarget();
            if (stacksMode)
            {
                string lo = lowerVal.HasValue ? StacksPreview(lowerVal.Value, target) : "PSC_Always".Translate().ToString();
                string hi = upperVal.HasValue ? StacksPreview(upperVal.Value, target) : "PSC_Maximum".Translate().ToString();
                return lo + " - " + hi;
            }

            string lower = lowerVal.HasValue ? FormatItemsPreview(lowerVal.Value, target) : "PSC_Always".Translate().ToString();
            string upper = upperVal.HasValue ? FormatItemsPreview(upperVal.Value, target) : "PSC_Maximum".Translate().ToString();
            return lower + " - " + upper;
        }

        public PscDefLimit ToLimit(ThingDef def)
        {
            int mult = stacksMode ? Mathf.Max(1, def.stackLimit) : 1;
            int? upper = upperVal.HasValue ? Mathf.Max(1, upperVal.Value) * mult : (int?)null;
            int? lower = lowerVal.HasValue ? Mathf.Max(0, lowerVal.Value) * mult : (int?)null;
            if (upper.HasValue && lower.HasValue && lower.Value > upper.Value) lower = upper;
            return new PscDefLimit { Upper = upper, Lower = lower };
        }

        // Pooled (group) read-out: the stored value IS the raw item total, so no stack conversion.
        public PscDefLimit ToRawLimit()
        {
            int? upper = upperVal.HasValue ? Mathf.Max(1, upperVal.Value) : (int?)null;
            int? lower = lowerVal.HasValue ? Mathf.Max(0, lowerVal.Value) : (int?)null;
            if (upper.HasValue && lower.HasValue && lower.Value > upper.Value) lower = upper;
            return new PscDefLimit { Upper = upper, Lower = lower };
        }

        public void LoadFrom(PscDefLimit lim, PscLimitEditorTarget target = null)
        {
            target ??= new PscLimitEditorTarget();
            lowerVal = lim != null && lim.Lower.HasValue ? lim.Lower.Value : (int?)null;
            upperVal = lim != null && lim.Upper.HasValue ? lim.Upper.Value : (int?)null;
            // Pooled: the stored value is already in the group's unit, so no conversion. The caller sets
            // stacksMode from the group's countMode before calling LoadFrom; preserve it.
            if (pooled) { SyncBuffers(); return; }
            // Default to stacks mode so the displayed unit stays stable as the selection changes (the
            // player can toggle to items when a single stack size makes it available). The stored value
            // is in items, so convert for display when we have a stack basis; fall back to items only
            // when there is no stack context to convert against.
            stacksMode = target.HasStackContext;
            if (stacksMode) ConvertItemsToStacks(target);
            SyncBuffers();
        }

        private void DrawEditorRow(Rect row, int sliderMax, PscLimitEditorTarget target)
        {
            float fieldW = PscUiTheme.FieldWidth;
            Rect lowerLabel = new Rect(row.x, row.y, fieldW, PscUiTheme.EditorLabelHeight);
            Rect upperLabel = new Rect(row.xMax - fieldW, row.y, fieldW, PscUiTheme.EditorLabelHeight);
            Rect lowerField = new Rect(row.x, row.y + PscUiTheme.FieldTopOffset, fieldW, PscUiTheme.FieldHeight);
            Rect upperField = new Rect(row.xMax - fieldW, row.y + PscUiTheme.FieldTopOffset, fieldW, PscUiTheme.FieldHeight);
            Rect sliderRect = new Rect(lowerField.xMax + PscUiTheme.SliderSideGap, row.y + PscUiTheme.SliderTopOffset,
                row.width - fieldW * 2f - PscUiTheme.SliderSideGap * 2f, PscUiTheme.SliderHeight);
            Rect hintRect = new Rect(sliderRect.x, row.y + PscUiTheme.HintTopOffset, sliderRect.width, PscUiTheme.HintHeight);

            var pf = Text.Font;
            var pa = Text.Anchor;
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(lowerLabel, "PSC_LowerShort".Translate());
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(upperLabel, "PSC_UpperShort".Translate());
            Text.Font = pf;
            Text.Anchor = pa;

            // In-window help: a faint "?" beside each header; hovering the header shows the tip.
            PscUiWidgets.DrawHelpIcon(new Rect(lowerLabel.xMax - 14f, lowerLabel.y, 14f, lowerLabel.height));
            PscUiWidgets.DrawHelpIcon(new Rect(upperLabel.x, upperLabel.y, 14f, upperLabel.height));
            TooltipHandler.TipRegion(lowerLabel, "PSC_RefillHelp".Translate());
            TooltipHandler.TipRegion(upperLabel, "PSC_MaxHelp".Translate());

            DrawNullableField(lowerField, ref lowerVal, ref lowerBuf, 0, sliderMax);
            DrawNullableField(upperField, ref upperVal, ref upperBuf, 1, sliderMax);

            if (PscLimitSlider.Draw(sliderRect, sliderMax, target, stacksMode, ref lowerVal, ref upperVal)) SyncBuffers();
            ClampValues(sliderMax);
            SyncBuffers();

            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = PscUiTheme.HintText;
            Widgets.Label(hintRect, SliderHint(target));
            GUI.color = Color.white;
            Text.Font = pf;
            Text.Anchor = pa;
        }

        internal static void DrawNullableField(Rect rect, ref int? value, ref string buffer, int min, int max)
        {
            string edited = Widgets.TextField(rect, buffer ?? "");
            if (edited == buffer) return;
            if (edited.NullOrEmpty())
            {
                buffer = "";
                value = null;
            }
            else if (int.TryParse(edited, out int parsed))
            {
                int clamped = Mathf.Clamp(parsed, min, max);
                value = clamped;
                buffer = clamped.ToString();
            }
        }

        private void ToggleMode(PscLimitEditorTarget target)
        {
            // Non-pooled mixed disables items mode (the button is greyed), so it never reaches here; the
            // guard is defensive. Pooled always allows the toggle.
            if (!pooled && !target.ItemsModeAllowed) return;
            // Convert the value only when there is a SINGLE stack factor (uniform members). A mixed pooled
            // group has no single factor, so the toggle just reinterprets the number in the new unit.
            if (target.HasStackContext && !target.mixedStackLimits)
            {
                int stack = target.StackLimit;
                if (stacksMode)
                {
                    lowerVal = lowerVal.HasValue ? lowerVal.Value * stack : (int?)null;
                    upperVal = upperVal.HasValue ? upperVal.Value * stack : (int?)null;
                }
                else
                {
                    lowerVal = lowerVal.HasValue ? Mathf.CeilToInt(lowerVal.Value / (float)stack) : (int?)null;
                    upperVal = upperVal.HasValue ? Mathf.CeilToInt(upperVal.Value / (float)stack) : (int?)null;
                }
            }
            stacksMode = !stacksMode;
            SyncBuffers();
        }

        private void ConvertItemsToStacks(PscLimitEditorTarget target)
        {
            if (!target.HasStackContext) return;
            int stack = target.StackLimit;
            lowerVal = lowerVal.HasValue ? Mathf.CeilToInt(lowerVal.Value / (float)stack) : (int?)null;
            upperVal = upperVal.HasValue ? Mathf.CeilToInt(upperVal.Value / (float)stack) : (int?)null;
        }

        private void ClampValues(int max) => PscLimitSlider.Clamp(ref lowerVal, ref upperVal, max);

        private void SyncBuffers()
        {
            lowerBuf = lowerVal.HasValue ? lowerVal.Value.ToString() : "";
            upperBuf = upperVal.HasValue ? upperVal.Value.ToString() : "";
        }

        private int SliderMax(PscHaulUnit unit, PscLimitEditorTarget target)
        {
            int slots = StackSlots(unit);
            // Pooled (group): stacks -> the unit's slot cap; items -> slots * the largest member stackLimit.
            // The items ceiling is a GENEROUS UI bound (not a precise mixed-capacity proof — a mixed-size
            // group can still express a number impossible for some compositions), but it keeps the slider in
            // a sane range and makes the items-mode ticks land on stack boundaries (so the tick count
            // matches stacks mode instead of comb-striding a 0-5000 range).
            if (pooled) return stacksMode ? Mathf.Max(1, slots) : Mathf.Max(1, slots * target.maxStackLimit);
            if (stacksMode) return Mathf.Max(1, slots);
            if (target.HasStackContext) return Mathf.Max(1, slots * target.StackLimit);
            return GenericItemMax;
        }

        // Shares PscHaulUnit.TryGetStackSlots with the paste-time capacity clamp so the slider cap
        // and the clamp use the identical capacity basis. Falls back to 1 for a unit with no cells.
        private static int StackSlots(PscHaulUnit unit)
            => unit.TryGetStackSlots(out int slots) ? slots : 1;

        private string SliderHint(PscLimitEditorTarget target)
        {
            if (pooled) return (stacksMode ? "PSC_SliderHintGroupStacks" : "PSC_SliderHintGroup").Translate();
            if (target.mixedStackLimits) return "PSC_SliderHintMixedStacks".Translate();
            if (!target.HasStackContext)
            {
                return "PSC_SliderHintGlobal".Translate();
            }
            return "PSC_SliderHintItem".Translate(target.StackLimit.ToString());
        }

        private static string FormatItemsPreview(int value, PscLimitEditorTarget target)
        {
            return target.HasStackContext ? PscUiWidgets.FormatItemsStacks(value, target.StackLimit) : value.ToString();
        }

        // "N stacks" — occupied stacks (cells) for a group. For a uniform-stack pooled group, also show the
        // max items those stacks can hold ("N stacks (up to M items)"), since a cell can be partly filled.
        private string StacksPreview(int stacks, PscLimitEditorTarget target)
        {
            string s = stacks + " " + "PSC_ModeStacks".Translate();
            if (pooled && target.HasStackContext && !target.mixedStackLimits)
                s += " (" + "PSC_StacksAsItems".Translate(stacks * target.StackLimit) + ")";
            return s;
        }
    }
}
