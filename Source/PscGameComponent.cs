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
            PscMigration.ClearPending();
            PscFeederHaulContext.ClearAll();   // routes are keyed by live Things; never inherit a prior session's
            PscCarriedSourceTracker.ClearAll();   // bulk-hauler carried-item provenance; runtime-only, same lifecycle
            PscHaulUnit.ClearIdCache();        // id cache is keyed by live group objects; same lifecycle
        }

        // Runs after every map's FinalizeInit (Game.FinalizeInit -> GameComponentUtility.FinalizeInit),
        // so storage units, cells and capacity are fully resolvable. Converts any limits captured from
        // a removed stockpile-limit mod during load (one-way migration) and notifies the player.
        public override void FinalizeInit()
        {
            base.FinalizeInit();
            PscMigration.ResolveAllPending();
        }

        // Periodic, self-throttled reconcile of the bulk-hauler carried-source provenance tracker against live
        // pawn inventories, so a segment that the store search never looks up again after the final unload (and
        // a dead/emptied pawn) is dropped instead of lingering until its age backstop. No-op (one int compare)
        // when nothing is captured, which is the case for every colony without a bulk inventory-hauler + feeder.
        public override void GameComponentTick()
        {
            PscCarriedSourceTracker.Tick(GenTicks.TicksGame);
        }
    }
}
