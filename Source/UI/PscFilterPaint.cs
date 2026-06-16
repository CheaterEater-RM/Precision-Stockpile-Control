using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Right-button row interaction for the vanilla storage filter (PSC-owned; left-button stays
    // pure vanilla allow/disallow + paint). A right-tap on a row opens the limit submenu; a
    // right-drag propagates the start row's limit (allow + set) across rows the cursor passes — the
    // "click-drag limit propagation" feature, on the right button to avoid fighting vanilla's
    // left-button checkbox paint. All access is guarded by the callers' try/catch so a UI fault can
    // never break the vanilla filter.
    internal static class PscFilterPaint
    {
        private static bool active;
        private static StorageSettings settings;
        private static PscHaulUnit unit;
        private static ThingDef startDef;
        private static PscDefLimit captured;   // null if the start row had no limit
        private static Vector2 startPos;
        private static bool dragged;
        private static bool changedAny;
        private static readonly HashSet<ThingDef> applied = new HashSet<ThingDef>();

        public static bool Active => active;

        public static void BeginRight(StorageSettings s, PscHaulUnit u, ThingDef def, PscDefLimit limit, Vector2 mousePos)
        {
            active = true;
            settings = s;
            unit = u;
            startDef = def;
            captured = (limit != null && !limit.IsDefault) ? limit.Clone() : null;
            startPos = mousePos;
            dragged = false;
            changedAny = false;
            applied.Clear();
        }

        // Per visible row, while a right-drag is in progress.
        public static void PaintRow(ThingDef def, Vector2 mousePos)
        {
            if (!active) return;
            if (!Input.GetMouseButton(1)) { Finish(); return; }   // button released off-list
            if (captured == null || def == null) return;
            if ((mousePos - startPos).sqrMagnitude > 25f) dragged = true;  // ~5px threshold
            if (!dragged || !applied.Add(def)) return;
            PscEdit.ApplyLimit(settings, unit, def, captured.Clone());
            changedAny = true;
        }

        // On right mouse-up: a tap (no drag) opens the submenu for the start def.
        public static void EndRight()
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

        private static void Finish()
        {
            if (changedAny && settings != null) PscMapComponent.NotifyPolicyChanged(settings);
            active = false;
            settings = null;
            unit = default;
            startDef = null;
            captured = null;
            dragged = false;
            changedAny = false;
            applied.Clear();
        }
    }
}
