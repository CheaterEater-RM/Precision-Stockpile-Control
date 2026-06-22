using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace PrecisionStockpileControl
{
    // The dual-handle (lower / upper) slider used by PscLimitEditor: rail + ticks draw, hit-testing,
    // and drag interaction. Operates directly on the editor's nullable value pair via ref so the
    // editor keeps ownership of the values; drag identity is global (one active slider at a time), so
    // the in-progress drag state is static here, mirroring vanilla's slider widgets.
    //
    // The rail's two extreme ends are the "blank" ends (always-refill / fill-to-maximum); a value
    // inside the NullBuffer at either end means "unset". DrawTicks marks both blank ends and the first
    // concrete value just inside them.
    internal static class PscLimitSlider
    {
        private enum DragEnd : byte { None, Lower, Upper }

        private static int draggingId;
        private static DragEnd draggingEnd;

        // Draws the slider and processes drag events. Returns true when lowerVal/upperVal changed.
        public static bool Draw(Rect rect, int sliderMax, PscLimitEditorTarget target, bool stacksMode,
            ref int? lowerVal, ref int? upperVal)
        {
            int id = StableId(rect);
            Rect rail = new Rect(rect.x + PscUiTheme.SliderRailInset, rect.center.y - PscUiTheme.SliderRailHalfHeight,
                rect.width - PscUiTheme.SliderRailInset * 2f, PscUiTheme.SliderRailHeight);
            PscUiWidgets.DrawSliderRail(rail);
            DrawTicks(rail, sliderMax, target, stacksMode);

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
                changed = SetDraggedValue(rail, sliderMax, target, stacksMode, ref lowerVal, ref upperVal);
                SoundDefOf.DragSlider.PlayOneShotOnCamera();
                e.Use();
            }
            else if (draggingId == id && e.type == EventType.MouseDrag)
            {
                changed = SetDraggedValue(rail, sliderMax, target, stacksMode, ref lowerVal, ref upperVal);
                e.Use();
            }
            return changed;
        }

        // Clamps the pair into range and enforces lower <= upper. Shared with the editor's field/init
        // paths so the invariant lives in one place.
        public static void Clamp(ref int? lowerVal, ref int? upperVal, int max)
            => PscDefLimit.ClampPair(ref lowerVal, ref upperVal, max);

        // Stable per-rect id so an in-progress drag survives across immediate-mode layout passes.
        private static int StableId(Rect rect)
            => Mathf.RoundToInt(rect.x * 17f + rect.y * 31f + rect.width * 43f + rect.height * 59f);

        private static bool SetDraggedValue(Rect rail, int sliderMax, PscLimitEditorTarget target, bool stacksMode,
            ref int? lowerVal, ref int? upperVal)
        {
            float norm = Mathf.InverseLerp(rail.xMin, rail.xMax, Event.current.mousePosition.x);
            if (draggingEnd == DragEnd.Lower)
            {
                int? next = NormToValue(norm, true, sliderMax, target, stacksMode);
                if (next == lowerVal) return false;
                lowerVal = next;
            }
            else if (draggingEnd == DragEnd.Upper)
            {
                int? next = NormToValue(norm, false, sliderMax, target, stacksMode);
                if (next == upperVal) return false;
                upperVal = next;
            }
            Clamp(ref lowerVal, ref upperVal, sliderMax);
            return true;
        }

        private static void DrawTicks(Rect rail, int sliderMax, PscLimitEditorTarget target, bool stacksMode)
        {
            // Endpoints (extreme rail ends) are the "blank" ends — always-refill / fill-to-maximum.
            // Bright and thick so they read as the special unbounded ends.
            DrawTick(rail, 0f, PscUiTheme.TickEndHeight, PscUiTheme.LimitColor, PscUiTheme.TickEndWidth);
            DrawTick(rail, 1f, PscUiTheme.TickEndHeight, PscUiTheme.LimitColor, PscUiTheme.TickEndWidth);

            // The first "real" value on each side sits just inside the null buffer. Mark these with the
            // limit-text accent so the jump from "blank end" to "first concrete value" is legible.
            DrawTick(rail, PscUiTheme.NullBuffer, PscUiTheme.TickFirstHeight, PscUiTheme.LimitTextColor, PscUiTheme.TickFirstWidth);
            DrawTick(rail, 1f - PscUiTheme.NullBuffer, PscUiTheme.TickFirstHeight, PscUiTheme.LimitTextColor, PscUiTheme.TickFirstWidth);

            if (!stacksMode && !target.HasStackContext) return;
            int step = stacksMode ? 1 : target.StackLimit;
            int stackTickCount = sliderMax / step + (sliderMax % step == 0 ? 0 : 1);
            int stride = TickCombStride(stackTickCount);
            long tickStepLong = (long)step * stride;
            if (tickStepLong > int.MaxValue) return;

            int tickStep = (int)tickStepLong;
            for (int v = tickStep; v < sliderMax; v += tickStep)
            {
                DrawTick(rail, ValueToNorm(v, true, sliderMax), PscUiTheme.TickMinorHeight, PscUiTheme.TickMinor, PscUiTheme.TickMinorWidth);
            }
        }

        private static int TickCombStride(int stackTickCount)
        {
            int stride = 1;
            while ((long)stackTickCount > (long)PscUiTheme.TickCombThreshold * stride && stride <= int.MaxValue / 2)
            {
                stride *= 2;
            }
            return stride;
        }

        private static void DrawTick(Rect rail, float norm, float height, Color color, float width = PscUiTheme.TickDefaultWidth)
        {
            var prev = GUI.color;
            GUI.color = color;
            float x = Mathf.Lerp(rail.xMin, rail.xMax, norm);
            GUI.DrawTexture(new Rect(x - width / 2f, rail.center.y - height / 2f, width, height), BaseContent.WhiteTex);
            GUI.color = prev;
        }

        private static Rect HandleRect(Rect rail, float norm)
        {
            float x = Mathf.Lerp(rail.xMin, rail.xMax, norm);
            return new Rect(x - PscUiTheme.SliderHandleHalf, rail.center.y - PscUiTheme.SliderHandleHalf,
                PscUiTheme.SliderHandleSize, PscUiTheme.SliderHandleSize);
        }

        private static float ValueToNorm(int? value, bool lower, int max)
        {
            if (!value.HasValue) return lower ? 0f : 1f;
            float clamped = Mathf.Clamp(value.Value, 0, max);
            return Mathf.Lerp(PscUiTheme.NullBuffer, 1f - PscUiTheme.NullBuffer, max <= 0 ? 0f : clamped / max);
        }

        private static int? NormToValue(float norm, bool lower, int max, PscLimitEditorTarget target, bool stacksMode)
        {
            if (lower && norm <= PscUiTheme.NullBuffer * 0.5f) return null;
            if (!lower && norm >= 1f - PscUiTheme.NullBuffer * 0.5f) return null;
            float t = Mathf.InverseLerp(PscUiTheme.NullBuffer, 1f - PscUiTheme.NullBuffer, Mathf.Clamp01(norm));
            int raw = Mathf.RoundToInt(t * max);
            if (!lower) raw = Mathf.Max(1, raw);
            return StickyStackValue(raw, max, target, stacksMode);
        }

        private static int StickyStackValue(int raw, int max, PscLimitEditorTarget target, bool stacksMode)
        {
            if (!target.HasStackContext || stacksMode) return Mathf.Clamp(raw, 0, max);
            int stack = target.StackLimit;
            int stick = Mathf.Max(1, Mathf.RoundToInt(stack * PscUiTheme.StickyStackFraction));
            for (int boundary = stack; boundary < max; boundary += stack)
            {
                if (raw > boundary && raw <= boundary + stick) return boundary;
            }
            return Mathf.Clamp(raw, 0, max);
        }
    }
}
