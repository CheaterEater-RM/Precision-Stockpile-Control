using Verse;

namespace PrecisionStockpileControl
{
    // Owns lifecycle clearing of the rebuildable static StorageData map. RimWorld constructs a
    // fresh Game (and therefore a fresh PscGameComponent) for both "new game" and "load save"
    // before any StorageSettings are scribed, so clearing in the ctor guarantees entries from a
    // previous session never leak across loads. The map is then repopulated by the
    // StorageSettings.ExposeData postfix during map load.
    public class PscGameComponent : GameComponent
    {
        public PscGameComponent(Game game)
        {
            PscStorageDataStore.Clear();
        }
    }
}
