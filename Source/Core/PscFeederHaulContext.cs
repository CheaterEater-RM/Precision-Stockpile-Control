using System.Collections.Generic;
using Verse;

namespace PrecisionStockpileControl
{
    // Runtime-only route context for a feeder haul. Planning sees a spawned item in its source
    // storage, but vanilla revalidates the destination after pickup when the item is carried and
    // no longer has a current SlotGroup. This preserves the planned source -> destination edge.
    public static class PscFeederHaulContext
    {
        public struct Route
        {
            public Map map;
            public string sourceId;
            public string destId;
        }

        private static readonly Dictionary<Thing, Route> routes = new Dictionary<Thing, Route>();

        // True when no feeder haul is in flight. The cheapest gate for the game-wide spawn/carry/
        // received seams that only need to clear or transfer a route — when empty there is nothing
        // to do, so they skip all per-Thing work.
        public static bool IsEmpty => routes.Count == 0;

        // Cleared on every new-game / load by PscGameComponent's ctor (routes is keyed by live Thing
        // objects; a fresh Game must not inherit the previous session's entries — they would pin dead
        // Things alive). Mirrors PscStorageDataStore.Clear()'s rebuildable-static-state lifecycle.
        public static void ClearAll() => routes.Clear();

        public static void Register(Thing thing, Map map, string sourceId, string destId)
        {
            if (thing == null || map == null || sourceId == null || destId == null)
                return;
            routes[thing] = new Route { map = map, sourceId = sourceId, destId = destId };
            if (PscLog.Enabled)
                PscLog.Msg($"feeder: ctx register {thing.def?.defName} ({sourceId} -> {destId})");
        }

        public static bool TryGet(Thing thing, out Route route)
        {
            if (thing != null && routes.TryGetValue(thing, out route))
                return true;
            route = default;
            return false;
        }

        public static void Transfer(Thing from, Thing to)
        {
            if (from == null || to == null || from == to) return;
            if (!routes.TryGetValue(from, out var route)) return;
            routes[to] = route;
            routes.Remove(from);
            if (PscLog.Enabled)
                PscLog.Msg($"feeder: ctx transfer {from.def?.defName} -> carried {to.def?.defName} ({route.sourceId} -> {route.destId})");
        }

        public static void Clear(Thing thing)
        {
            // Clear is called for every haul job (most aren't feeder routes); only log when a route
            // actually existed, so the common no-op stays silent even with logging on.
            if (thing != null && routes.Remove(thing) && PscLog.Enabled)
                PscLog.Msg($"feeder: ctx cleared route for {thing.def?.defName}");
        }

        public static void ClearForEndpoint(string id)
        {
            if (id == null || routes.Count == 0) return;
            List<Thing> remove = null;
            foreach (var kv in routes)
            {
                if (kv.Value.sourceId == id || kv.Value.destId == id)
                    (remove ??= new List<Thing>()).Add(kv.Key);
            }
            if (remove != null)
                foreach (var t in remove)
                    routes.Remove(t);
        }

        public static void PruneForMap(Map map, PscMapComponent psc)
        {
            if (map == null || psc == null || routes.Count == 0) return;
            List<Thing> remove = null;
            foreach (var kv in routes)
            {
                var route = kv.Value;
                if (route.map == map && !psc.HasFunctionalFeederEdge(route.sourceId, route.destId))
                    (remove ??= new List<Thing>()).Add(kv.Key);
            }
            if (remove != null)
                foreach (var t in remove)
                    routes.Remove(t);
        }

        // Drop every in-flight route on a map being removed (caravan reform / settlement abandon /
        // temporary map). Unlike PruneForMap (which only drops non-functional edges), this clears all
        // of the map's routes unconditionally so a Route's Map/Thing references can't pin the dead
        // map's object graph alive until the next load. Called from PscMapComponent.MapRemoved.
        public static void ClearForMap(Map map)
        {
            if (map == null || routes.Count == 0) return;
            List<Thing> remove = null;
            foreach (var kv in routes)
                if (kv.Value.map == map)
                    (remove ??= new List<Thing>()).Add(kv.Key);
            if (remove != null)
                foreach (var t in remove)
                    routes.Remove(t);
        }

        public static bool IsPlannedDestination(Thing thing, PscHaulUnit target)
        {
            return TryGet(thing, out var route)
                && target.IsValid
                && target.Map == route.map
                && target.UniqueLoadID == route.destId;
        }
    }
}
