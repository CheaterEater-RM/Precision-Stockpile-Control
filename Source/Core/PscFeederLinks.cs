using System.Collections.Generic;
using Verse;

namespace PrecisionStockpileControl
{
    // One directed feeder edge: items flow from sourceId -> destId. Endpoints are PscHaulUnit
    // UniqueLoadID string handles (D7). Stored as plain strings, never Scribe_References, so an
    // unresolved endpoint (removed storage, removed mod) drops silently with no abstract-class
    // unload hazard.
    public class PscFeederLink : IExposable
    {
        public string sourceId;
        public string destId;

        public PscFeederLink() { }
        public PscFeederLink(string sourceId, string destId) { this.sourceId = sourceId; this.destId = destId; }

        public void ExposeData()
        {
            Scribe_Values.Look(ref sourceId, "src");
            Scribe_Values.Look(ref destId, "dst");
        }
    }

    // Authoritative directed-edge store for one map's feeder links (design §4.2). The List is the
    // single source of truth (scribed by PscMapComponent); the runtime indices below are derived
    // and rebuilt lazily behind `dirty`. A link binds a PAIR of haul units, so it cannot live on
    // the per-unit (group-shared) StorageSettings — it lives here.
    //
    // All membership/adjacency work is by string id (zero unit resolution); only the overlay needs
    // to resolve ids back to live units, which it does itself.
    public class PscFeederLinks : IExposable
    {
        private List<PscFeederLink> links = new List<PscFeederLink>();

        private readonly HashSet<(string, string)> edgeSet = new HashSet<(string, string)>();
        private readonly Dictionary<string, List<string>> destsBySource = new Dictionary<string, List<string>>();
        private readonly Dictionary<string, List<string>> sourcesByDest = new Dictionary<string, List<string>>();
        private bool dirty = true;

        // Bumped on every edge mutation. The overlay's port-layout cache (PscFeederLayout) reads this
        // so it can rebuild only when the route set actually changed, not every frame. Runtime-only,
        // never scribed. All mutations route through MarkDirty() so no site can forget to bump it.
        private int generation;
        public int Generation => generation;
        private void MarkDirty() { dirty = true; generation++; }

        public bool IsEmpty => links.Count == 0;
        public List<PscFeederLink> Links => links;   // read-only iteration for the overlay

        // An edge is well-formed when both endpoint ids are present. Self-loops are rejected separately
        // at AddEdge time (a persisted self-loop, if one ever existed, is harmless to index/query).
        private static bool IsValidEdge(string sourceId, string destId)
            => !string.IsNullOrEmpty(sourceId) && !string.IsNullOrEmpty(destId);

        private static bool IsValidEdge(PscFeederLink l) => l != null && IsValidEdge(l.sourceId, l.destId);

        // ---- derived index ----
        private void EnsureIndex()
        {
            if (!dirty) return;
            edgeSet.Clear();
            destsBySource.Clear();
            sourcesByDest.Clear();
            foreach (var l in links)
            {
                if (!IsValidEdge(l)) continue;
                edgeSet.Add((l.sourceId, l.destId));
                AddAdj(destsBySource, l.sourceId, l.destId);
                AddAdj(sourcesByDest, l.destId, l.sourceId);
            }
            dirty = false;
        }

        private static void AddAdj(Dictionary<string, List<string>> map, string key, string val)
        {
            if (!map.TryGetValue(key, out var list)) { list = new List<string>(); map[key] = list; }
            if (!list.Contains(val)) list.Add(val);
        }

        // ---- queries ----
        public bool HasEdge(string sourceId, string destId)
        {
            if (!IsValidEdge(sourceId, destId)) return false;
            EnsureIndex();
            return edgeSet.Contains((sourceId, destId));
        }

        public bool HasAnySource(string id) { if (string.IsNullOrEmpty(id)) return false; EnsureIndex(); return sourcesByDest.ContainsKey(id); }
        public bool HasAnyDestination(string id) { if (string.IsNullOrEmpty(id)) return false; EnsureIndex(); return destsBySource.ContainsKey(id); }

        // Snapshot one unit's endpoint ids (for the copy/paste clipboard payload).
        public void CollectEndpoints(string id, List<string> sourcesOut, List<string> destsOut)
        {
            if (string.IsNullOrEmpty(id)) return;
            EnsureIndex();
            if (sourcesByDest.TryGetValue(id, out var srcs)) sourcesOut.AddRange(srcs);
            if (destsBySource.TryGetValue(id, out var dsts)) destsOut.AddRange(dsts);
        }

        // Hop distance (0 = a seed) from `seeds` along the feeder graph, for the overlay's chain
        // highlight. downDist follows OUTGOING edges (destsBySource) — what the seeds feed into;
        // upDist follows INCOMING edges (sourcesByDest) — what feeds the seeds. Unbounded; the
        // overlay fades opacity per hop and floors it, so a deep chain is naturally bounded.
        // Caller owns and clears nothing — we clear the three scratch collections ourselves and
        // reuse them across frames, so this allocates nothing steady-state (Perf Rules).
        public void ComputeChainDistances(HashSet<string> seeds,
            Dictionary<string, int> downDist, Dictionary<string, int> upDist, Queue<string> scratchQueue)
        {
            downDist.Clear();
            upDist.Clear();
            if (seeds == null || seeds.Count == 0) return;
            EnsureIndex();
            Bfs(seeds, destsBySource, downDist, scratchQueue);
            Bfs(seeds, sourcesByDest, upDist, scratchQueue);
        }

        private static void Bfs(HashSet<string> seeds, Dictionary<string, List<string>> adj,
            Dictionary<string, int> dist, Queue<string> queue)
        {
            queue.Clear();
            foreach (var s in seeds)
            {
                if (string.IsNullOrEmpty(s) || dist.ContainsKey(s)) continue;
                dist[s] = 0;
                queue.Enqueue(s);
            }
            while (queue.Count > 0)
            {
                string cur = queue.Dequeue();
                int next = dist[cur] + 1;
                if (!adj.TryGetValue(cur, out var neighbours)) continue;
                for (int i = 0; i < neighbours.Count; i++)
                {
                    string n = neighbours[i];
                    if (dist.ContainsKey(n)) continue;
                    dist[n] = next;
                    queue.Enqueue(n);
                }
            }
        }

        // ---- mutations (all mark the index dirty) ----
        public bool AddEdge(string sourceId, string destId)
        {
            if (!IsValidEdge(sourceId, destId) || sourceId == destId) return false;
            if (HasEdge(sourceId, destId)) return false;
            links.Add(new PscFeederLink(sourceId, destId));
            MarkDirty();
            return true;
        }

        public bool RemoveEdge(string sourceId, string destId)
        {
            int n = links.RemoveAll(l => l.sourceId == sourceId && l.destId == destId);
            if (n > 0) MarkDirty();
            return n > 0;
        }

        public bool RemoveAllFor(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            int n = links.RemoveAll(l => l.sourceId == id || l.destId == id);
            if (n > 0) MarkDirty();
            return n > 0;
        }

        public bool ClearAll()
        {
            if (links.Count == 0) return false;
            links.Clear();
            MarkDirty();
            return true;
        }

        // Reciprocal duplication: every edge touching fromId is mirrored onto toId, so the adopting
        // unit gains the same sources and destinations (copy-paste "duplicate", vanilla-link first-
        // member adoption). This is the prompt's "update across more than one stockpile's pairs."
        public void AdoptLinks(string fromId, string toId)
        {
            if (!IsValidEdge(fromId, toId) || fromId == toId) return;
            // Snapshot first — AddEdge mutates `links` while we read it.
            List<PscFeederLink> additions = null;
            foreach (var l in links)
            {
                if (l.sourceId == fromId && l.destId != toId)
                    (additions ??= new List<PscFeederLink>()).Add(new PscFeederLink(toId, l.destId));
                if (l.destId == fromId && l.sourceId != toId)
                    (additions ??= new List<PscFeederLink>()).Add(new PscFeederLink(l.sourceId, toId));
            }
            if (additions == null) return;
            foreach (var e in additions) AddEdge(e.sourceId, e.destId);
        }

        // Drop edges whose endpoints are not live storage units on the map. Called on load to
        // self-heal leaks from removed storage (and to keep cross-map paste garbage from lingering).
        public bool PruneToLiveIds(HashSet<string> liveIds)
        {
            if (liveIds == null) return false;
            int n = links.RemoveAll(l => !liveIds.Contains(l.sourceId) || !liveIds.Contains(l.destId));
            if (n > 0) MarkDirty();
            return n > 0;
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref links, "links", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (links == null) links = new List<PscFeederLink>();
                links.RemoveAll(l => !IsValidEdge(l));
                MarkDirty();
            }
        }
    }
}
