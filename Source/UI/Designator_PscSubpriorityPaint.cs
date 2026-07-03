using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Sequential subpriority painter (a QoL tool over the fine-order letter, design §9). Activated from
    // a selected storage (the SEED), it walks the a-z subpriority downward onto every OTHER storage the
    // player paints: seed "b" -> first painted "c" -> "d" -> ... Each painted unit adopts the seed's
    // band + sub-tier (so the whole sequence shares one level and the letters sort coherently) plus the
    // next letter. Stops at "z".
    //
    //   Click : act on the one storage under the cursor.
    //   Drag  : PAINT — every storage the cursor passes over takes the next letter (one per UNIT, not
    //           per cell). No-backtracking: a unit painted this session is never re-assigned, so a
    //           drag-over or double-click can't knock the sequence out of order.
    //
    // Mirrors Designator_PscFeederLink: a Designator with NO draw style (SelectedStyle null -> single
    // cell on mouse-down + SelectedUpdate paint while held). The session is anchored to the seed's
    // order AT ACTIVATION — a manual edit to the seed mid-session does not retarget the running letter
    // (the tool's state is sovereign). All session state is transient; nothing here is scribed.
    public class Designator_PscSubpriorityPaint : Designator
    {
        // Edge-highlight colours so the player can tell the seed / already-painted / about-to-paint
        // units apart during the session (the council's "did I paint that?" guard).
        private static readonly Color SeedColor = new Color(0.45f, 0.65f, 1f, 0.85f);
        private static readonly Color PaintedColor = new Color(0.40f, 0.90f, 0.45f, 0.75f);
        private static readonly Color HoverColor = new Color(1f, 0.95f, 0.45f, 0.95f);

        private readonly PscPaintTarget seed;
        private readonly StoragePriority band;   // seed's band, frozen for the session
        private readonly byte subTier;           // seed's sub-tier, frozen for the session
        private readonly string seedId;
        private string currentLetter;            // last letter assigned; starts as the seed's letter

        private readonly HashSet<string> painted = new HashSet<string>();   // dedup by target Id
        private readonly List<PscPaintTarget> paintedUnits = new List<PscPaintTarget>(); // for highlights
        private readonly List<IntVec3> cellBuffer = new List<IntVec3>();    // reused per frame, never retains CellsList
        private IntVec3 lastPaintedCell = IntVec3.Invalid;

        public Designator_PscSubpriorityPaint(PscPaintTarget seed, StoragePriority band, byte subTier,
            string startingLetter, Texture2D icon)
        {
            this.seed = seed;
            this.band = band;
            this.subTier = subTier;
            this.currentLetter = string.IsNullOrEmpty(startingLetter) ? null : startingLetter;
            this.icon = icon;
            defaultLabel = "PSC_PaintSubpriority".Translate();
            defaultDesc = "PSC_PaintSubpriorityDesc".Translate();
            useMouseIcon = true;
            soundSucceeded = SoundDefOf.Click;
            seedId = seed.Id;
            if (seedId != null) painted.Add(seedId);  // the seed never repaints itself
        }

        private Map TargetMap => seed.Map;

        public override AcceptanceReport CanDesignateCell(IntVec3 c)
        {
            var map = TargetMap;
            if (map == null || !c.InBounds(map)) return false;
            if (!PscPaintTarget.TryResolveCell(c, map, out var u) || u.SameAs(seed)) return false;
            // Do NOT reject when the sequence is exhausted (currentLetter == z): the click must still
            // reach DesignateSingleCell so it can show the message and exit the tool (the chosen
            // "message + exit" behaviour). Already-painted units are rejected here (no backtracking).
            string id = u.Id;
            return id == null || !painted.Contains(id);
        }

        public override void DesignateSingleCell(IntVec3 c)
        {
            lastPaintedCell = c;
            // Defense in depth: the launching gizmo is hidden while a-z subpriorities are off, but if the
            // setting is flipped off mid-drag, stop painting rather than write now-inert letters.
            if (PscMod.Settings == null || !PscMod.Settings.subpriorityLetters)
            {
                Find.DesignatorManager.Deselect();
                return;
            }
            var map = TargetMap;
            if (map == null) return;
            if (!PscPaintTarget.TryResolveCell(c, map, out var other) || other.SameAs(seed)) return;
            string id = other.Id;
            if (id == null || painted.Contains(id)) return;       // no backtracking

            // Reached the bottom: assign nothing more, tell the player once, and exit the tool.
            if (PscOrder.LetterIsZ(currentLetter))
            {
                Messages.Message("PSC_PaintClamp".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                Find.DesignatorManager.Deselect();
                return;
            }

            string next = PscOrder.StepPaintLetter(currentLetter, out _);
            PscOrder.ApplyPaintStep(other.Settings, band, subTier, next);
            currentLetter = next;
            painted.Add(id);
            paintedUnits.Add(other);
        }

        public override void SelectedUpdate()
        {
            var map = TargetMap;
            if (map == null || Find.CurrentMap != map) { lastPaintedCell = IntVec3.Invalid; return; }

            DrawSessionHighlights(map);

            // Paint while the left button is held — link each new cell the cursor passes over. The
            // per-unit dedup in CanDesignateCell/DesignateSingleCell makes dragging over a multi-cell
            // unit a single paint.
            if (Mouse.IsInputBlockedNow || !Input.GetMouseButton(0)) { lastPaintedCell = IntVec3.Invalid; return; }
            IntVec3 cell = UI.MouseCell();
            if (cell == lastPaintedCell) return;
            lastPaintedCell = cell;
            if (CanDesignateCell(cell).Accepted) DesignateSingleCell(cell);
        }

        // Outline the seed, the already-painted units, and the hovered paintable unit, so the running
        // sequence is legible even with the standard PSC overlay toggled off. Drawn every frame the tool
        // is selected (mesh edges belong in the Update phase, like vanilla area designators).
        private void DrawSessionHighlights(Map map)
        {
            DrawTargetEdges(seed, SeedColor);

            // Combine painted units into a single field-edge call (CellsList is a shared static temp, so
            // copy each unit's cells out into our buffer before the next fetch overwrites it).
            cellBuffer.Clear();
            for (int i = 0; i < paintedUnits.Count; i++)
            {
                var cells = paintedUnits[i].Cells;
                if (cells != null) cellBuffer.AddRange(cells);
            }
            if (cellBuffer.Count > 0) GenDraw.DrawFieldEdges(cellBuffer, PaintedColor);

            if (Mouse.IsInputBlockedNow) return;
            IntVec3 c = UI.MouseCell();
            if (!c.InBounds(map)) return;
            if (!PscPaintTarget.TryResolveCell(c, map, out var hover) || hover.SameAs(seed)) return;
            string id = hover.Id;
            if (id != null && painted.Contains(id)) return;
            DrawTargetEdges(hover, HoverColor);
        }

        private static void DrawTargetEdges(in PscPaintTarget u, Color color)
        {
            var cells = u.Cells;   // built: static temp — passed to DrawFieldEdges immediately, never retained
            if (cells == null || cells.Count == 0) return;
            GenDraw.DrawFieldEdges(cells, color);
        }

        public override void DrawMouseAttachments()
        {
            string preview = PscOrder.LetterIsZ(currentLetter)
                ? "–"
                : PscOrder.StepPaintLetter(currentLetter, out _);
            GenUI.DrawMouseAttachment(icon, "PSC_PaintNext".Translate(preview));
        }
    }
}
