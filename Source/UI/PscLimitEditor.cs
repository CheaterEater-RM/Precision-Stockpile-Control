using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace PrecisionStockpileControl
{
    public class PscLimitEditorTarget
    {
        public ThingDef exactDef;
        public int? sharedStackLimit;
        public int? firstStackLimit;
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
    public class PscLimitEditor
    {
        private const float NullBuffer = 0.06f;
        private const int GenericItemMax = 5000;

        private enum DragEnd : byte { None, Lower, Upper }

        private static int draggingId;
        private static DragEnd draggingEnd;

        public bool stacksMode = true;
        public int? lowerVal;
        public int? upperVal;
        public string lowerBuf = "";
        public string upperBuf = "";

        public void Draw(Listing_Standard list, PscHaulUnit unit, PscLimitEditorTarget target = null)
        {
            target ??= new PscLimitEditorTarget();
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
            if (!target.ItemsModeAllowed)
            {
                TooltipHandler.TipRegion(modeButton, "PSC_ItemsModeMixedStacksTip".Translate());
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.75f, 0.75f, 0.65f);
                list.Label("PSC_ItemsModeMixedStacksTip".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }

            int sliderMax = SliderMax(unit, target);
            ClampValues(sliderMax);

            list.Gap(6f);
            Rect row = list.GetRect(82f);
            DrawEditorRow(row, sliderMax, target);
            list.Gap(2f);
        }

        public string PreviewString(PscLimitEditorTarget target = null)
        {
            target ??= new PscLimitEditorTarget();
            if (stacksMode)
            {
                string lo = lowerVal.HasValue ? lowerVal.Value + " " + "PSC_ModeStacks".Translate() : "PSC_Always".Translate().ToString();
                string hi = upperVal.HasValue ? upperVal.Value + " " + "PSC_ModeStacks".Translate() : "PSC_Maximum".Translate().ToString();
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

        public void LoadFrom(PscDefLimit lim, PscLimitEditorTarget target = null)
        {
            target ??= new PscLimitEditorTarget();
            stacksMode = false;
            lowerVal = lim != null && lim.Lower.HasValue ? lim.Lower.Value : (int?)null;
            upperVal = lim != null && lim.Upper.HasValue ? lim.Upper.Value : (int?)null;
            if (!target.ItemsModeAllowed)
            {
                ConvertItemsToStacks(target);
                stacksMode = true;
            }
            SyncBuffers();
        }

        private void DrawEditorRow(Rect row, int sliderMax, PscLimitEditorTarget target)
        {
            const float fieldW = 72f;
            Rect lowerLabel = new Rect(row.x, row.y, fieldW, 20f);
            Rect upperLabel = new Rect(row.xMax - fieldW, row.y, fieldW, 20f);
            Rect lowerField = new Rect(row.x, row.y + 22f, fieldW, 28f);
            Rect upperField = new Rect(row.xMax - fieldW, row.y + 22f, fieldW, 28f);
            Rect sliderRect = new Rect(lowerField.xMax + 12f, row.y + 26f,
                row.width - fieldW * 2f - 24f, 24f);
            Rect hintRect = new Rect(sliderRect.x, row.y + 54f, sliderRect.width, 24f);

            var pf = Text.Font;
            var pa = Text.Anchor;
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(lowerLabel, "PSC_LowerShort".Translate());
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(upperLabel, "PSC_UpperShort".Translate());
            Text.Font = pf;
            Text.Anchor = pa;

            DrawNullableField(lowerField, ref lowerVal, ref lowerBuf, 0, sliderMax);
            DrawNullableField(upperField, ref upperVal, ref upperBuf, 1, sliderMax);

            if (DrawDualSlider(sliderRect, sliderMax, target)) SyncBuffers();
            ClampValues(sliderMax);
            SyncBuffers();

            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.UpperCenter;
            GUI.color = new Color(0.72f, 0.72f, 0.72f);
            Widgets.Label(hintRect, SliderHint(target));
            GUI.color = Color.white;
            Text.Font = pf;
            Text.Anchor = pa;
        }

        private bool DrawDualSlider(Rect rect, int sliderMax, PscLimitEditorTarget target)
        {
            int id = Mathf.RoundToInt(rect.x * 17f + rect.y * 31f + rect.width * 43f + rect.height * 59f);
            Rect rail = new Rect(rect.x + 6f, rect.center.y - 4f, rect.width - 12f, 8f);
            PscUiWidgets.DrawSliderRail(rail);
            DrawTicks(rail, sliderMax, target);

            float lowerN = ValueToNorm(lowerVal, true, sliderMax);
            float upperN = ValueToNorm(upperVal, false, sliderMax);
            Rect lowerHandle = HandleRect(rail, lowerN);
            Rect upperHandle = HandleRect(rail, upperN);

            PscUiWidgets.DrawSliderHandle(lowerHandle);
            PscUiWidgets.DrawSliderHandle(upperHandle);

            var e = Event.current;
            if (e == null) return false;
            if ((e.type == EventType.MouseUp || e.rawType == EventType.MouseUp) && draggingId == id)
            {
                draggingId = 0;
                draggingEnd = DragEnd.None;
                SoundDefOf.DragSlider.PlayOneShotOnCamera();
                return false;
            }

            bool changed = false;
            if (e.type == EventType.MouseDown && e.button == 0 && Mouse.IsOver(rect))
            {
                draggingId = id;
                float x = e.mousePosition.x;
                draggingEnd = Mathf.Abs(x - lowerHandle.center.x) <= Mathf.Abs(x - upperHandle.center.x)
                    ? DragEnd.Lower : DragEnd.Upper;
                changed = SetDraggedValue(rail, sliderMax, target);
                SoundDefOf.DragSlider.PlayOneShotOnCamera();
                e.Use();
            }
            else if (draggingId == id && e.type == EventType.MouseDrag)
            {
                changed = SetDraggedValue(rail, sliderMax, target);
                e.Use();
            }
            return changed;
        }

        private bool SetDraggedValue(Rect rail, int sliderMax, PscLimitEditorTarget target)
        {
            float norm = Mathf.InverseLerp(rail.xMin, rail.xMax, Event.current.mousePosition.x);
            if (draggingEnd == DragEnd.Lower)
            {
                int? next = NormToValue(norm, true, sliderMax, target);
                if (next == lowerVal) return false;
                lowerVal = next;
            }
            else if (draggingEnd == DragEnd.Upper)
            {
                int? next = NormToValue(norm, false, sliderMax, target);
                if (next == upperVal) return false;
                upperVal = next;
            }
            ClampValues(sliderMax);
            return true;
        }

        private void DrawTicks(Rect rail, int sliderMax, PscLimitEditorTarget target)
        {
            DrawTick(rail, 0f, 18f, PscUiWidgets.LimitColor);
            DrawTick(rail, NullBuffer, 12f, Color.gray);
            DrawTick(rail, 1f - NullBuffer, 12f, Color.gray);
            DrawTick(rail, 1f, 18f, PscUiWidgets.LimitColor);

            if (!stacksMode && !target.HasStackContext) return;
            int step = stacksMode ? 1 : target.StackLimit;
            int maxTicks = 24;
            int tickCount = sliderMax / step;
            int stride = Mathf.Max(1, Mathf.CeilToInt(tickCount / (float)maxTicks));
            for (int v = step * stride; v < sliderMax; v += step * stride)
            {
                DrawTick(rail, ValueToNorm(v, true, sliderMax), 10f, new Color(0.55f, 0.55f, 0.55f));
            }
        }

        private static void DrawTick(Rect rail, float norm, float height, Color color)
        {
            var prev = GUI.color;
            GUI.color = color;
            float x = Mathf.Lerp(rail.xMin, rail.xMax, norm);
            GUI.DrawTexture(new Rect(x - 1f, rail.center.y - height / 2f, 2f, height), BaseContent.WhiteTex);
            GUI.color = prev;
        }

        private static Rect HandleRect(Rect rail, float norm)
        {
            float x = Mathf.Lerp(rail.xMin, rail.xMax, norm);
            return new Rect(x - 6f, rail.center.y - 6f, 12f, 12f);
        }

        private static float ValueToNorm(int? value, bool lower, int max)
        {
            if (!value.HasValue) return lower ? 0f : 1f;
            float clamped = Mathf.Clamp(value.Value, 0, max);
            return Mathf.Lerp(NullBuffer, 1f - NullBuffer, max <= 0 ? 0f : clamped / max);
        }

        private int? NormToValue(float norm, bool lower, int max, PscLimitEditorTarget target)
        {
            if (lower && norm <= NullBuffer * 0.5f) return null;
            if (!lower && norm >= 1f - NullBuffer * 0.5f) return null;
            float t = Mathf.InverseLerp(NullBuffer, 1f - NullBuffer, Mathf.Clamp01(norm));
            int raw = Mathf.RoundToInt(t * max);
            if (!lower) raw = Mathf.Max(1, raw);
            return StickyStackValue(raw, max, target);
        }

        private int StickyStackValue(int raw, int max, PscLimitEditorTarget target)
        {
            if (!target.HasStackContext || stacksMode) return Mathf.Clamp(raw, 0, max);
            int stack = target.StackLimit;
            int stick = Mathf.Max(1, Mathf.RoundToInt(stack * 0.1f));
            for (int boundary = stack; boundary < max; boundary += stack)
            {
                if (raw > boundary && raw <= boundary + stick) return boundary;
            }
            return Mathf.Clamp(raw, 0, max);
        }

        private static void DrawNullableField(Rect rect, ref int? value, ref string buffer, int min, int max)
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
            if (!target.ItemsModeAllowed) return;
            if (target.HasStackContext)
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

        private void ClampValues(int max)
        {
            if (lowerVal.HasValue) lowerVal = Mathf.Clamp(lowerVal.Value, 0, max);
            if (upperVal.HasValue) upperVal = Mathf.Clamp(upperVal.Value, 1, max);
            if (lowerVal.HasValue && upperVal.HasValue && lowerVal.Value > upperVal.Value) lowerVal = upperVal;
        }

        private void SyncBuffers()
        {
            lowerBuf = lowerVal.HasValue ? lowerVal.Value.ToString() : "";
            upperBuf = upperVal.HasValue ? upperVal.Value.ToString() : "";
        }

        private int SliderMax(PscHaulUnit unit, PscLimitEditorTarget target)
        {
            int slots = StackSlots(unit);
            if (stacksMode) return Mathf.Max(1, slots);
            if (target.HasStackContext) return Mathf.Max(1, slots * target.StackLimit);
            return GenericItemMax;
        }

        private static int StackSlots(PscHaulUnit unit)
        {
            int slots = 0;
            try
            {
                var map = unit.Map;
                var cells = unit.IsValid ? unit.group.CellsList : null;
                if (cells != null)
                {
                    foreach (var c in cells)
                    {
                        slots += map != null ? c.GetMaxItemsAllowedInCell(map) : 1;
                    }
                }
                var held = unit.HeldThings;
                if (held != null)
                {
                    int heldStacks = 0;
                    foreach (var t in held)
                    {
                        if (t != null) heldStacks++;
                    }
                    if (heldStacks > slots) slots = heldStacks;
                }
            }
            catch { }
            return Mathf.Max(1, slots);
        }

        private string SliderHint(PscLimitEditorTarget target)
        {
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
    }

    // Single place that mutates a unit's policy for one def + keeps the vanilla filter in sync.
    // Callers batch several defs then call PscMapComponent.NotifyPolicyChanged once.
    internal static class PscEdit
    {
        public static void ApplyLimit(StorageSettings settings, PscHaulUnit unit, ThingDef def, PscDefLimit lim)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            if (lim == null || lim.IsDefault) data.limits.Remove(def);
            else data.limits[def] = lim;
            settings.filter.SetAllow(def, true);
            data.Notify_LimitSet(def, unit);
        }

        public static void ClearLimit(StorageSettings settings, ThingDef def, bool allow)
        {
            PscStorageDataStore.TryGet(settings)?.limits.Remove(def);
            settings.filter.SetAllow(def, allow);
        }

        public static void ClearAllLimits(StorageSettings settings)
        {
            var data = PscStorageDataStore.TryGet(settings);
            if (data?.limits == null || data.limits.Count == 0) return;
            data.limits.Clear();
            PscMapComponent.NotifyPolicyChanged(settings);
        }
    }
}
