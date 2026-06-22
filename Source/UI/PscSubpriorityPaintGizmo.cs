using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // The "Paint subpriority" gizmo on a selected stockpile / shelf (design §9). Activating it seeds a
    // Designator_PscSubpriorityPaint from this storage's CURRENT band + sub-tier + letter, then lets the
    // player click/drag other storage to walk the a-z subpriority downward across them. Only the letter
    // sequence is new; the gizmo is a thin launcher in the PscFeederGizmos mould.
    //
    // Icon reuses the vanilla rename pencil as a placeholder (custom art is tracked in 05_WISHLIST.md).
    [StaticConstructorOnStartup]
    public static class PscSubpriorityPaintGizmo
    {
        private static readonly Texture2D PaintTex =
            ContentFinder<Texture2D>.Get("UI/Buttons/Rename", reportFailure: false)
            ?? ContentFinder<Texture2D>.Get("UI/Commands/SelectAllLinked", reportFailure: false)
            ?? BaseContent.BadTex;

        public static IEnumerable<Gizmo> GizmosFor(StorageSettings settings, PscHaulUnit unit)
        {
            if (settings == null || !unit.IsValid || unit.UniqueLoadID == null) yield break;
            var data = PscStorageDataStore.TryGet(settings);

            yield return new Command_Action
            {
                icon = PaintTex,
                defaultLabel = "PSC_PaintSubpriority".Translate(),
                defaultDesc = "PSC_PaintSubpriorityDesc".Translate(),
                action = () => Find.DesignatorManager.Select(new Designator_PscSubpriorityPaint(
                    unit, settings.Priority, data?.subTier ?? 0, data?.letter, PaintTex))
            };
        }
    }
}
