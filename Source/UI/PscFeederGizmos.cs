using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // The feeder gizmos shown on a selected stockpile / shelf (design §10.6, design-goals §UI):
    // Set source, Set destination, Clear route (paint tool; right-click for bulk clears),
    // Pull-only (toggle), Push-only (toggle).
    //
    // Icons currently reuse vanilla command textures (custom art is tracked in 05_WISHLIST.md).
    [StaticConstructorOnStartup]
    public static class PscFeederGizmos
    {
        private static readonly Texture2D ConnectSourceTex = Load("UI/Commands/LinkStorageSettings");
        private static readonly Texture2D ConnectDestTex = Load("UI/Commands/SelectAllLinked");
        private static readonly Texture2D OnlyFromTex = Load("UI/Commands/LinkStorageSettings");
        private static readonly Texture2D OnlyToTex = Load("UI/Commands/SelectAllLinked");
        private static readonly Texture2D BreakTex = Load("UI/Designators/Cancel");

        private static Texture2D Load(string path) => ContentFinder<Texture2D>.Get(path, reportFailure: false) ?? BaseContent.BadTex;

        public static IEnumerable<Gizmo> GizmosFor(StorageSettings settings, PscHaulUnit unit)
        {
            if (settings == null || !unit.IsValid) yield break;
            var psc = PscMapComponent.For(unit.Map);
            if (psc == null) yield break;
            string id = unit.UniqueLoadID;
            if (id == null) yield break;

            // Connect source / destination: paint designators (single click links one storage; drag
            // paints — every storage the cursor passes over is linked). Designator.ProcessInput self-selects.
            yield return new Designator_PscFeederLink(unit, PscFeederLinkMode.Source, ConnectSourceTex);
            yield return new Designator_PscFeederLink(unit, PscFeederLinkMode.Destination, ConnectDestTex);
            yield return new Designator_PscFeederLink(unit, PscFeederLinkMode.Break, BreakTex);

            var onlyFrom = new Command_Toggle
            {
                defaultLabel = "PSC_OnlyFromSource".Translate(),
                defaultDesc = "PSC_OnlyFromSourceDesc".Translate(),
                icon = OnlyFromTex,
                isActive = () => PscStorageDataStore.TryGet(settings)?.onlyFromSource ?? false,
                toggleAction = () => ToggleFlag(settings, fromSource: true)
            };
            if (!psc.Links.HasAnySource(id)) onlyFrom.Disable("PSC_NoSourceReason".Translate());
            yield return onlyFrom;

            var onlyTo = new Command_Toggle
            {
                defaultLabel = "PSC_OnlyToDestinations".Translate(),
                defaultDesc = "PSC_OnlyToDestinationsDesc".Translate(),
                icon = OnlyToTex,
                isActive = () => PscStorageDataStore.TryGet(settings)?.onlyToDestinations ?? false,
                toggleAction = () => ToggleFlag(settings, fromSource: false)
            };
            if (!psc.Links.HasAnyDestination(id)) onlyTo.Disable("PSC_NoDestinationReason".Translate());
            yield return onlyTo;

            // The overlay (all routes / panels) is toggled by the bottom-right play-settings button
            // (PlaySettings_GlobalControls_Patch) — no per-storage "Show routes" gizmo any more.
            // Route-clearing (this storage / whole map) lives on the "Clear route" tool's right-click
            // menu (Designator_PscFeederLink), not a separate button.
        }

        private static void ToggleFlag(StorageSettings settings, bool fromSource)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            if (data == null) return;
            if (fromSource) data.onlyFromSource = !data.onlyFromSource;
            else data.onlyToDestinations = !data.onlyToDestinations;
            PscMapComponent.NotifyPolicyChanged(settings);
        }

    }
}
