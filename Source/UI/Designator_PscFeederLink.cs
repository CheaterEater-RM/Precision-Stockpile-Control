using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Click or click-drag to designate storages as a source / destination of `self`.
    //   Click  : link the one storage under the cursor.
    //   Drag   : PAINT — every storage the cursor passes over is linked immediately (not a box you
    //            draw out and apply on release). Lets the player run across several stockpiles to link
    //            each without clicking them one by one.
    //
    // Implemented as a Designator with NO draw style: with SelectedStyle null the manager applies a
    // single cell on mouse-down (and suppresses normal selection), and we paint the rest in
    // SelectedUpdate while the button stays held. Stays active until right-click / ESC.
    public class Designator_PscFeederLink : Designator
    {
        private readonly PscHaulUnit self;
        private readonly bool asSource;
        private IntVec3 lastPaintedCell = IntVec3.Invalid;

        public Designator_PscFeederLink(PscHaulUnit self, bool asSource, Texture2D icon)
        {
            this.self = self;
            this.asSource = asSource;
            this.icon = icon;
            defaultLabel = (asSource ? "PSC_ConnectSource" : "PSC_ConnectDestination").Translate();
            defaultDesc = (asSource ? "PSC_ConnectSourceDesc" : "PSC_ConnectDestinationDesc").Translate();
            useMouseIcon = true;
            soundSucceeded = SoundDefOf.Click;
        }
        // Intentionally no DrawStyleCategory override -> SelectedStyle stays null -> single-cell
        // mouse-down application, no rectangle/area drag.

        private Map TargetMap => self.Map;

        public override AcceptanceReport CanDesignateCell(IntVec3 c)
        {
            var map = TargetMap;
            if (map == null || !c.InBounds(map)) return false;
            var u = PscHaulUnit.ResolveCell(c, map);
            return u.IsValid && !u.Equals(self);
        }

        public override void DesignateSingleCell(IntVec3 c)
        {
            lastPaintedCell = c;
            var map = TargetMap;
            if (map == null) return;
            var other = PscHaulUnit.ResolveCell(c, map);
            if (!other.IsValid || other.Equals(self)) return;
            var psc = PscMapComponent.For(map);
            if (psc == null) return;
            if (asSource) psc.AddFeederLink(other, self);   // picked unit feeds self
            else psc.AddFeederLink(self, other);            // self feeds picked unit
        }

        // Paint while the left button is held: link each new cell the cursor passes over. (AddFeederLink
        // dedups, so passing repeatedly over the same storage is a no-op.)
        public override void SelectedUpdate()
        {
            if (Mouse.IsInputBlockedNow || Find.CurrentMap != TargetMap)
            {
                lastPaintedCell = IntVec3.Invalid;
                return;
            }
            if (!Input.GetMouseButton(0))
            {
                lastPaintedCell = IntVec3.Invalid;
                return;
            }
            IntVec3 c = UI.MouseCell();
            if (c == lastPaintedCell) return;
            lastPaintedCell = c;
            if (CanDesignateCell(c).Accepted) DesignateSingleCell(c);
        }
    }
}
