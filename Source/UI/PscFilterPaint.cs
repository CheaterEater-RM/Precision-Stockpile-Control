using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // PSC-owned row interaction for the vanilla storage filter. Limited rows use left-click/drag
    // on the marker; right-click/right-drag remains as a compatibility path.
    internal static class PscFilterPaint
    {
        private static bool active;
        private static int button = -1;
        private static StorageSettings settings;
        private static PscHaulUnit unit;
        private static ThingDef startDef;
        private static PscDefLimit captured;   // null if the start row had no limit
        private static float? capturedLowerStacks;
        private static float? capturedUpperStacks;
        private static Vector2 startPos;
        private static bool dragged;
        private static bool changedAny;
        private static bool ownedCheckboxActive;
        private static Rect ownedCheckboxRect;
        private static readonly HashSet<ThingDef> applied = new HashSet<ThingDef>();

        public static bool Active => active;
        public static int Button => button;

        public static void Begin(int mouseButton, StorageSettings s, PscHaulUnit u, ThingDef def, PscDefLimit limit, Vector2 mousePos)
        {
            active = true;
            button = mouseButton;
            settings = s;
            unit = u;
            startDef = def;
            captured = (limit != null && !limit.IsDefault) ? limit.Clone() : null;
            int sourceStack = Mathf.Max(1, def?.stackLimit ?? 1);
            capturedLowerStacks = captured != null && captured.Lower.HasValue ? captured.Lower.Value / (float)sourceStack : (float?)null;
            capturedUpperStacks = captured != null && captured.Upper.HasValue ? captured.Upper.Value / (float)sourceStack : (float?)null;
            startPos = mousePos;
            dragged = false;
            changedAny = false;
            applied.Clear();
        }

        public static void PaintRow(ThingDef def, Vector2 mousePos)
        {
            if (!active) return;
            if (button < 0 || !Input.GetMouseButton(button)) { Finish(); return; }
            if (captured == null || def == null) return;
            if ((mousePos - startPos).sqrMagnitude > 25f) dragged = true;
            if (!dragged || !applied.Add(def)) return;
            PscEdit.ApplyLimit(settings, unit, def, LimitFor(def));
            changedAny = true;
        }

        public static void End()
        {
            if (!active) return;
            if (!dragged && startDef != null && settings != null)
            {
                Find.WindowStack.WindowOfType<PscItemLimitMenu>()?.Close(false);
                Find.WindowStack.Add(new PscItemLimitMenu(settings, unit,
                    new List<ThingDef> { startDef }, startDef.LabelCap));
            }
            Finish();
        }

        public static void OwnCheckbox(Rect rect)
        {
            ownedCheckboxActive = true;
            ownedCheckboxRect = rect;
        }

        public static void ClearOwnedCheckbox()
        {
            ownedCheckboxActive = false;
            ownedCheckboxRect = default;
        }

        public static bool ShouldSuppressCheckbox(Rect rect)
        {
            return ownedCheckboxActive
                && Mathf.Abs(rect.x - ownedCheckboxRect.x) < 0.5f
                && Mathf.Abs(rect.y - ownedCheckboxRect.y) < 0.5f
                && Mathf.Abs(rect.width - ownedCheckboxRect.width) < 0.5f
                && Mathf.Abs(rect.height - ownedCheckboxRect.height) < 0.5f;
        }

        private static PscDefLimit LimitFor(ThingDef def)
        {
            if (def == startDef) return captured.Clone();
            int stack = Mathf.Max(1, def?.stackLimit ?? 1);
            int? lower = capturedLowerStacks.HasValue
                ? Mathf.Max(0, Mathf.RoundToInt(capturedLowerStacks.Value * stack))
                : (int?)null;
            int? upper = capturedUpperStacks.HasValue
                ? Mathf.Max(1, Mathf.RoundToInt(capturedUpperStacks.Value * stack))
                : (int?)null;
            if (lower.HasValue && upper.HasValue && lower.Value > upper.Value) lower = upper;
            return new PscDefLimit { Lower = lower, Upper = upper };
        }

        private static void Finish()
        {
            if (changedAny && settings != null) PscMapComponent.NotifyPolicyChanged(settings);
            active = false;
            button = -1;
            settings = null;
            unit = default;
            startDef = null;
            captured = null;
            capturedLowerStacks = null;
            capturedUpperStacks = null;
            dragged = false;
            changedAny = false;
            applied.Clear();
        }
    }
}
