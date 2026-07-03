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
    // Icon loads PSC art from UI/Subpriority/PSC_Paint; a missing file shows BadTex on purpose, as a
    // visible "art not drawn yet" signal rather than a vanilla stand-in.
    [StaticConstructorOnStartup]
    public static class PscSubpriorityPaintGizmo
    {
        private static readonly Texture2D PaintTex =
            ContentFinder<Texture2D>.Get("UI/Subpriority/PSC_Paint", reportFailure: false)
            ?? BaseContent.BadTex;

        // `parent` is the launching storage (built unit, or an unbuilt Blueprint_Storage / Frame); the
        // seed target is resolved from it so the tool works the same launched from a blueprint or a
        // built shelf. `unit` is the built unit (invalid for an unbuilt parent).
        public static IEnumerable<Gizmo> GizmosFor(IStoreSettingsParent parent, StorageSettings settings, PscHaulUnit unit)
        {
            // The gizmo's only function is painting a-z letters, so it's hidden while the a-z
            // subpriority is off (the level/band remain on the storage tab).
            if (PscMod.Settings == null || !PscMod.Settings.subpriorityLetters) yield break;
            if (settings == null) yield break;
            if (!PscPaintTarget.TryFromParent(parent, settings, unit, out var seed)) yield break;
            var data = PscStorageDataStore.TryGet(settings);

            yield return new Command_Action
            {
                icon = PaintTex,
                defaultLabel = "PSC_PaintSubpriority".Translate(),
                defaultDesc = "PSC_PaintSubpriorityDesc".Translate(),
                action = () => Find.DesignatorManager.Select(new Designator_PscSubpriorityPaint(
                    seed, settings.Priority, data?.subTier ?? 0, data?.letter, PaintTex))
            };
        }
    }
}
