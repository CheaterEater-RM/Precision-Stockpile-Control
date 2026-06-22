using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace PrecisionStockpileControl
{
    // What a feeder paint designator does to each storage the cursor touches.
    public enum PscFeederLinkMode { Source, Destination, Break }

    // Click or click-drag to designate storages as a source / destination of `self`, or to break
    // any connection between them.
    //   Click  : act on the one storage under the cursor.
    //   Drag   : PAINT — every storage the cursor passes over is acted on immediately (not a box you
    //            draw out and apply on release). Lets the player run across several stockpiles to link
    //            (or unlink) each without clicking them one by one.
    //
    // Implemented as a Designator with NO draw style: with SelectedStyle null the manager applies a
    // single cell on mouse-down (and suppresses normal selection), and we paint the rest in
    // SelectedUpdate while the button stays held. Stays active until right-click / ESC.
    public class Designator_PscFeederLink : Designator
    {
        private readonly PscHaulUnit self;
        private readonly PscFeederLinkMode mode;
        private IntVec3 lastPaintedCell = IntVec3.Invalid;

        public Designator_PscFeederLink(PscHaulUnit self, PscFeederLinkMode mode, Texture2D icon)
        {
            this.self = self;
            this.mode = mode;
            this.icon = icon;
            string key = mode switch
            {
                PscFeederLinkMode.Source => "PSC_ConnectSource",
                PscFeederLinkMode.Destination => "PSC_ConnectDestination",
                _ => "PSC_BreakConnection"
            };
            defaultLabel = key.Translate();
            defaultDesc = (key + "Desc").Translate();
            useMouseIcon = true;
            soundSucceeded = SoundDefOf.Click;
        }
        // Intentionally no DrawStyleCategory override -> SelectedStyle stays null -> single-cell
        // mouse-down application, no rectangle/area drag.

        // Right-clicking the "Clear route" tool pops the bulk-clear menu (the standalone "Clear all
        // routes" button was folded in here). Left-click / drag still breaks one route at a time;
        // right-click opens this menu instead of selecting the paint tool. Only the Break tool offers
        // it — the Set source / Set destination tools have no bulk equivalent.
        public override IEnumerable<FloatMenuOption> RightClickFloatMenuOptions
        {
            get
            {
                if (mode != PscFeederLinkMode.Break) yield break;
                var psc = PscMapComponent.For(self.Map);
                if (psc == null) yield break;
                yield return new FloatMenuOption("PSC_ClearConnectionsThisStockpile".Translate(), () =>
                {
                    psc.ClearFeederLinksFor(self);
                    SoundDefOf.Click.PlayOneShotOnCamera();
                });
                yield return new FloatMenuOption("PSC_ClearConnectionsConfirm".Translate(), () =>
                {
                    // Map-wide clear is destructive; gate it behind a confirmation dialog.
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        "PSC_ClearConnectionsConfirmDialog".Translate(),
                        () =>
                        {
                            psc.ClearAllFeederLinks();
                            SoundDefOf.Click.PlayOneShotOnCamera();
                        },
                        destructive: true));
                });
            }
        }

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
            switch (mode)
            {
                case PscFeederLinkMode.Source:                                            // picked unit feeds self
                    if (psc.AddFeederLink(other, self))
                    {
                        // Auto-priority FIRST, then seed strictness only if the route is functional now, so a
                        // nudge that fixes the order gets its flags but a still-dead route doesn't lock the
                        // piles (F4). Skip the dead-route warning when auto-priority already explained why.
                        bool messaged = AutoPriority(dest: self, source: other);
                        psc.SeedFeederStrictnessIfFunctional(other, self);
                        if (!messaged) WarnIfDeadRoute(psc, source: other, dest: self);
                    }
                    break;
                case PscFeederLinkMode.Destination:                                       // self feeds picked unit
                    if (psc.AddFeederLink(self, other))
                    {
                        bool messaged = AutoPriority(dest: other, source: self);
                        psc.SeedFeederStrictnessIfFunctional(self, other);
                        if (!messaged) WarnIfDeadRoute(psc, source: self, dest: other);
                    }
                    break;
                case PscFeederLinkMode.Break: psc.BreakFeederLink(self, other); break;    // drop any link between them
            }
        }

        // A route whose destination doesn't outrank its source carries nothing (D5). Auto-priority, when
        // enabled, nudges the painted unit so the route works at once; when it's off (the default) the
        // only other cue is the red overlay line — which the player may not have open. Surface a clear
        // message so a freshly-made dead route isn't a silent mystery, per "make the problem obvious
        // rather than divine the player's intent" (we deliberately do NOT auto-reorder their piles here).
        // Checked AFTER AutoPriority so we stay quiet when it already made the route functional. Constant
        // text => RimWorld collapses a paint-drag's repeats into one counted message instead of spamming.
        private void WarnIfDeadRoute(PscMapComponent psc, PscHaulUnit source, PscHaulUnit dest)
        {
            if (psc.HasFunctionalFeederEdge(source, dest)) return;
            Messages.Message("PSC_RouteNotFunctional".Translate(), MessageTypeDefOf.RejectInput, historical: false);
        }

        // Auto-priority (D4): on a freshly created link, nudge the PAINTED unit one fine-order letter
        // step onto the correct side of the SELECTED (anchor) unit so the link is functional at once.
        // The two directions are independent opt-in settings: Connect-source paints the source (steps it
        // DOWN below the anchor dest, gated on autosetSourcePriority); Connect-destination paints the dest
        // (steps it UP above the anchor source, gated on autosetDestinationPriority). Both off by default.
        // Only nudges WITHIN a band (letter steps); it never changes a unit's band (CrossBand → message),
        // and clamps at the band's letter range. Returns true when it surfaced a message (Clamped /
        // CrossBand), so the caller can skip the redundant dead-route warning.
        private bool AutoPriority(PscHaulUnit dest, PscHaulUnit source)
        {
            if (PscMod.Settings == null) return false;
            PscOrder.AutoOrderResult result;
            string clampKey;
            if (mode == PscFeederLinkMode.Source)
            {
                if (!PscMod.Settings.autosetSourcePriority) return false;
                result = PscOrder.PlaceSourceBelowDest(dest.Settings, source.Settings);   // painted = source, step down
                clampKey = "PSC_AutoPriorityClampLow";
            }
            else
            {
                if (!PscMod.Settings.autosetDestinationPriority) return false;
                result = PscOrder.PlaceDestAboveSource(source.Settings, dest.Settings);   // painted = dest, step up
                clampKey = "PSC_AutoPriorityClampHigh";
            }
            if (result == PscOrder.AutoOrderResult.Clamped)
            {
                Messages.Message(clampKey.Translate(), MessageTypeDefOf.RejectInput, historical: false);
                return true;
            }
            if (result == PscOrder.AutoOrderResult.CrossBand)
            {
                Messages.Message("PSC_AutoPriorityCrossBand".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                return true;
            }
            return false;
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
