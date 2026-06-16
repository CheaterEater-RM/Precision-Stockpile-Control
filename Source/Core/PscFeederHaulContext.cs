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

        public static void Register(Thing thing, Map map, string sourceId, string destId)
        {
            if (thing == null || map == null || sourceId == null || destId == null)
                return;
            routes[thing] = new Route { map = map, sourceId = sourceId, destId = destId };
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
        }

        public static void Clear(Thing thing)
        {
            if (thing != null) routes.Remove(thing);
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

        public static bool IsPlannedDestination(Thing thing, PscHaulUnit target)
        {
            return TryGet(thing, out var route)
                && target.IsValid
                && target.Map == route.map
                && target.UniqueLoadID == route.destId;
        }
    }
}
