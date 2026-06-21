using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Precomputed "port" layout for the feeder-route overlay (PscFeederOverlay). Instead of drawing
    // every route from one unit's centroid to another's centroid — which makes many routes converging
    // on the same unit stack into an unreadable web — each route gets an endpoint placed on the
    // storage unit's bounding-box perimeter, pointing toward its partner, with routes leaving/entering
    // in nearly the same direction fanned apart (angular de-clustering). Lines then fan out at each
    // unit instead of collapsing to a point.
    //
    // Rebuilt only when the route graph changes (PscFeederLinks.Generation), the drawn map changes, or
    // a unit's footprint may have moved (a cheap throttled rebuild) — NEVER per frame. Zero
    // steady-state allocation: grow-only dictionaries + pooled scratch lists, Clear()ed on rebuild
    // (the PscStorageOverlay pool discipline). CellsList is a static temporary (PscHaulUnit §5) — read
    // once into ints during a rebuild, never retained.
    public static class PscFeederLayout
    {
        // ---- tuning (dev can flip the feature off in settings; these constants are the look) ----
        private const float ClusterEpsRad = 0.20f;        // ~11.5°: routes closer than this share a "cluster"
        private const float SpreadDeg = 26f;              // fan width a small cluster is spread across
        private const float MaxArcDeg = 80f;              // cap on fan width for a crowded cluster
        private const float Eps = 0.0001f;
        private const int GeometryRecheckInterval = 30;   // frames between footprint rebuilds (~0.5s)

        private struct Box { public Vector3 center; public float hx, hz; }
        private struct Ports { public Vector3 src, dst; public bool srcSet, dstSet, valid; }
        private struct EdgeAngle { public int edgeIndex; public bool isSource; public float angle; }

        // Keyed by unit UniqueLoadID; ports keyed by the route's index in PscFeederLinks.Links.
        private static readonly Dictionary<string, Box> boxes = new Dictionary<string, Box>();
        private static readonly Dictionary<int, Ports> ports = new Dictionary<int, Ports>();
        private static readonly HashSet<string> neededIds = new HashSet<string>();

        // Per-unit incident-route angle buckets, pooled so a rebuild allocates nothing.
        private static readonly Dictionary<string, List<EdgeAngle>> incident = new Dictionary<string, List<EdgeAngle>>();
        private static readonly Stack<List<EdgeAngle>> listPool = new Stack<List<EdgeAngle>>();

        private static int builtGeneration = -1;
        private static int builtMapId = -1;
        private static int lastBuiltFrame = -1;

        // The route set's geometry is deterministic, so a rebuild with identical inputs yields
        // identical ports — the periodic rebuild below causes no visible jitter.
        public static void EnsureBuilt(Map map, PscMapComponent psc)
        {
            int gen = psc.Links.Generation;
            int frame = Time.frameCount;
            if (gen == builtGeneration && map.uniqueID == builtMapId
                && frame - lastBuiltFrame < GeometryRecheckInterval)
                return;

            Rebuild(map, psc);
            builtGeneration = gen;
            builtMapId = map.uniqueID;
            lastBuiltFrame = frame;
        }

        public static bool TryGetPorts(int edgeIndex, out Vector3 src, out Vector3 dst)
        {
            if (ports.TryGetValue(edgeIndex, out var p) && p.valid) { src = p.src; dst = p.dst; return true; }
            src = default; dst = default; return false;
        }

        private static void Rebuild(Map map, PscMapComponent psc)
        {
            boxes.Clear();
            ports.Clear();
            ReturnIncidentLists();

            var list = psc.Links.Links;

            // 1) which units actually participate in a route
            neededIds.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                var l = list[i];
                if (string.IsNullOrEmpty(l.sourceId) || string.IsNullOrEmpty(l.destId)) continue;
                neededIds.Add(l.sourceId);
                neededIds.Add(l.destId);
            }
            if (neededIds.Count == 0) return;

            // 2) bounding box for each participating unit (one CellsList read each, never retained)
            var groups = map.haulDestinationManager.AllGroupsListForReading;
            for (int i = 0; i < groups.Count; i++)
            {
                var u = PscHaulUnit.FromSlotGroup(groups[i]);
                if (!u.IsValid) continue;
                var id = u.UniqueLoadID;
                if (id == null || boxes.ContainsKey(id) || !neededIds.Contains(id)) continue;
                if (TryComputeBox(u, out var box)) boxes[id] = box;
            }

            // 3) bucket each route's outward direction at each of its (boxed) endpoints
            for (int i = 0; i < list.Count; i++)
            {
                var l = list[i];
                if (string.IsNullOrEmpty(l.sourceId) || string.IsNullOrEmpty(l.destId)) continue;
                if (!boxes.TryGetValue(l.sourceId, out var sb) || !boxes.TryGetValue(l.destId, out var db)) continue;
                Vector3 d = db.center - sb.center; d.y = 0f;
                float len = d.magnitude;
                if (len < 0.01f) continue;   // degenerate (overlapping units): draw falls back to centroid
                d /= len;
                AddIncident(l.sourceId, i, true, Mathf.Atan2(d.z, d.x));
                AddIncident(l.destId, i, false, Mathf.Atan2(-d.z, -d.x));
            }

            // 4) per unit: sort incident routes by angle, de-cluster, place each port on the perimeter
            foreach (var kv in incident)
                AssignPorts(boxes[kv.Key], kv.Value);
        }

        private static bool TryComputeBox(PscHaulUnit u, out Box box)
        {
            box = default;
            var cells = u.group?.CellsList;
            if (cells == null || cells.Count == 0) return false;
            int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
            for (int i = 0; i < cells.Count; i++)
            {
                var c = cells[i];
                if (c.x < minX) minX = c.x;
                if (c.x > maxX) maxX = c.x;
                if (c.z < minZ) minZ = c.z;
                if (c.z > maxZ) maxZ = c.z;
            }
            box.center = new Vector3((minX + maxX + 1) / 2f, 0f, (minZ + maxZ + 1) / 2f);
            box.hx = (maxX - minX + 1) / 2f;   // 1×1 -> 0.5 (still offsets toward the partner)
            box.hz = (maxZ - minZ + 1) / 2f;
            return true;
        }

        // Spread a unit's incident routes around its perimeter. Routes whose outward angles are within
        // ClusterEpsRad of each other are fanned across an arc centred on the cluster mean, so a tight
        // bundle (e.g. six sources roughly behind one destination) separates into distinct lines.
        private static void AssignPorts(Box box, List<EdgeAngle> l)
        {
            l.Sort(CompareByAngle);
            int k = l.Count;
            int i = 0;
            while (i < k)
            {
                int j = i + 1;
                while (j < k && l[j].angle - l[j - 1].angle < ClusterEpsRad) j++;
                int m = j - i;
                if (m == 1)
                {
                    SetPort(box, l[i], l[i].angle);
                }
                else
                {
                    float mean = 0f;
                    for (int t = i; t < j; t++) mean += l[t].angle;
                    mean /= m;
                    float arcDeg = m <= 3 ? SpreadDeg : Mathf.Min(MaxArcDeg, SpreadDeg * m / 3f);
                    float arc = arcDeg * Mathf.Deg2Rad;
                    float spacing = arc / (m - 1);
                    float start = mean - arc * 0.5f;
                    for (int t = 0; t < m; t++) SetPort(box, l[i + t], start + t * spacing);
                }
                i = j;
            }
        }

        // Port = where the ray from the box centre along `angle` exits the bounding box.
        private static void SetPort(Box box, EdgeAngle e, float angle)
        {
            float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
            float t = Mathf.Min(box.hx / Mathf.Max(Mathf.Abs(c), Eps), box.hz / Mathf.Max(Mathf.Abs(s), Eps));
            Vector3 pos = box.center + new Vector3(c, 0f, s) * t;
            ports.TryGetValue(e.edgeIndex, out var p);
            if (e.isSource) { p.src = pos; p.srcSet = true; }
            else { p.dst = pos; p.dstSet = true; }
            p.valid = p.srcSet && p.dstSet;
            ports[e.edgeIndex] = p;
        }

        private static readonly System.Comparison<EdgeAngle> CompareByAngle = (a, b) => a.angle.CompareTo(b.angle);

        private static void AddIncident(string id, int edgeIndex, bool isSource, float angle)
        {
            if (!incident.TryGetValue(id, out var l)) { l = listPool.Count > 0 ? listPool.Pop() : new List<EdgeAngle>(); incident[id] = l; }
            l.Add(new EdgeAngle { edgeIndex = edgeIndex, isSource = isSource, angle = angle });
        }

        private static void ReturnIncidentLists()
        {
            foreach (var l in incident.Values) { l.Clear(); listPool.Push(l); }
            incident.Clear();
        }
    }
}
