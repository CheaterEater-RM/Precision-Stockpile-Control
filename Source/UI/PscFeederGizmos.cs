using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace PrecisionStockpileControl
{
    // The six feeder gizmos shown on a selected stockpile / shelf (design §10.6, design-goals §UI):
    // Connect source, Connect destination, Only-from-source (toggle), Only-to-destinations (toggle),
    // Show connections (toggle), Clear all connections (right-click required).
    //
    // TODO(art): icons reuse vanilla command textures for now; swap in custom art later.
    [StaticConstructorOnStartup]
    public static class PscFeederGizmos
    {
        private static readonly Texture2D ConnectSourceTex = Load("UI/Commands/LinkStorageSettings");
        private static readonly Texture2D ConnectDestTex = Load("UI/Commands/SelectAllLinked");
        private static readonly Texture2D OnlyFromTex = Load("UI/Commands/LinkStorageSettings");
        private static readonly Texture2D OnlyToTex = Load("UI/Commands/SelectAllLinked");
        private static readonly Texture2D ShowTex = Load("UI/Commands/SelectAllLinked");
        private static readonly Texture2D ClearTex = Load("UI/Designators/Cancel");

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
            yield return new Designator_PscFeederLink(unit, asSource: true, ConnectSourceTex);
            yield return new Designator_PscFeederLink(unit, asSource: false, ConnectDestTex);

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

            yield return new Command_Toggle
            {
                defaultLabel = "PSC_ShowConnections".Translate(),
                defaultDesc = "PSC_ShowConnectionsDesc".Translate(),
                icon = ShowTex,
                isActive = () => PscFeederOverlay.ShowAll,
                toggleAction = () => PscFeederOverlay.ShowAll = !PscFeederOverlay.ShowAll
            };

            yield return new Command_ClearConnections
            {
                defaultLabel = "PSC_ClearConnections".Translate(),
                defaultDesc = "PSC_ClearConnectionsDesc".Translate(),
                icon = ClearTex,
                psc = psc,
                action = () => Messages.Message("PSC_ClearConnectionsHint".Translate(), MessageTypeDefOf.RejectInput, historical: false)
            };
        }

        private static void ToggleFlag(StorageSettings settings, bool fromSource)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            if (data == null) return;
            if (fromSource) data.onlyFromSource = !data.onlyFromSource;
            else data.onlyToDestinations = !data.onlyToDestinations;
            PscMapComponent.NotifyPolicyChanged(settings);
        }

        // Clear-all requires a right-click (a plain click only nudges the player). The actual clear
        // lives in the right-click float menu.
        private class Command_ClearConnections : Command_Action
        {
            public PscMapComponent psc;

            public override IEnumerable<FloatMenuOption> RightClickFloatMenuOptions
            {
                get
                {
                    yield return new FloatMenuOption("PSC_ClearConnectionsConfirm".Translate(), () =>
                    {
                        psc?.ClearAllFeederLinks();
                        SoundDefOf.Click.PlayOneShotOnCamera();
                    });
                }
            }
        }
    }
}
