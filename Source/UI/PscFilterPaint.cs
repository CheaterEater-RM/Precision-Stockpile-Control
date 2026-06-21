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

        // Vanilla's paintable-checkbox state (checkboxPainting / checkboxPaintingState) is read through
        // PscReflection so the private-field seam lives in one place. checkboxPainting is set when a
        // left-drag paint begins and reset on MouseUp; checkboxPaintingState is the value being painted
        // (true = allow). We read checkboxPainting directly (not Widgets.Painting) so a dropdown paint
        // can't false-trigger.
        private static bool vanillaPaintDirty;
        private static StorageSettings vanillaPaintSettings;

        public static bool Active => active;
        public static int Button => button;
        public static bool HasOwnedCheckbox => ownedCheckboxActive;

        // A vanilla left-drag allow/disallow paint is in progress and is NOT our own marker drag.
        public static bool VanillaPaintActive => !active && PscReflection.WidgetsCheckboxPainting && Input.GetMouseButton(0);
        public static bool VanillaPaintAllow => PscReflection.WidgetsCheckboxPaintingState;

        // Records that a vanilla paint cleared one or more limits on this storage. The expensive
        // NotifyPolicyChanged (rebuilds tracking + the early-out gates over all tracked units) is
        // deferred and flushed once per drag.
        public static void MarkVanillaPaintDirty(StorageSettings s)
        {
            vanillaPaintDirty = true;
            vanillaPaintSettings = s;
        }

        // Called every frame from the FillTab finalizer. Fires the single deferred NotifyPolicyChanged
        // once the paint drag has ended. If the tab closes mid-drag the flush is skipped; the data is
        // already correct and tracking self-heals on the next policy edit / load.
        public static void FlushPendingVanillaPaint()
        {
            if (!vanillaPaintDirty) return;
            if (PscReflection.WidgetsCheckboxPainting && Input.GetMouseButton(0)) return;
            if (vanillaPaintSettings != null) PscMapComponent.NotifyPolicyChanged(vanillaPaintSettings);
            vanillaPaintDirty = false;
            vanillaPaintSettings = null;
        }

        // Per-frame teardown from the FillTab finalizer. Only clears the owned-checkbox marker (re-set
        // each row next frame). It must NOT finish an active drag here based on Input.GetMouseButton:
        // that polls real-time hardware state, which races the queued MouseUp event. On a quick click the
        // button is physically up before MouseUp is processed, so finishing here would cancel the drag
        // before the row postfix can run End() (the click-to-edit path). Stale state is instead reaped by
        // PaintRow (self-heals when the button isn't held) and by Begin (clean-slate guard).
        public static void Reset()
        {
            ClearOwnedCheckbox();
        }

        public static void Begin(int mouseButton, StorageSettings s, PscHaulUnit u, ThingDef def, PscDefLimit limit, Vector2 mousePos)
        {
            if (active) Finish(); // clean slate: reap any state stranded by a tab closed mid-drag
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
