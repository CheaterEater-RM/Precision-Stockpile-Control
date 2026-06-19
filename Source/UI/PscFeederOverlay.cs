using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // On-map feeder-link overlay, drawn from PscMapComponent.MapComponentUpdate. Mirrors the
    // Contagion developer overlay's drawing style: MetaOverlay-shader lines via GenDraw.DrawLineBetween,
    // chevron arrowheads near both ends, and small × marks along non-functional links.
    //
    // Valid link (green): destination outranks source (D5) — items will actually flow.
    // Non-functional link (red + ×): destination priority <= source — vanilla won't haul it.
    public static class PscFeederOverlay
    {
        private static readonly Color GoodColor = new Color(0.30f, 0.92f, 0.34f, 0.85f);
        private static readonly Color BadColor = new Color(0.92f, 0.22f, 0.22f, 0.90f);
        private static readonly Dictionary<int, Material> MatCache = new Dictionary<int, Material>();

        // Global toggle driven by the "Show connections" gizmo (session state — not scribed).
        public static bool ShowAll;

        // Reused per-frame scratch so the draw path allocates nothing steady-state.
        private static readonly Dictionary<string, PscHaulUnit> idMap = new Dictionary<string, PscHaulUnit>();

        // Per-frame draw-center memo: a unit's centroid is deterministic within a frame, so compute
        // it once even when the unit is an endpoint of many incident links (turns the draw loop's
        // O(links × cells) into O(units × cells)). null = known "no center" so failures aren't
        // retried per link. Cleared at the top of each Draw() alongside idMap.
        private static readonly Dictionary<string, Vector3?> centerCache = new Dictionary<string, Vector3?>();

        public static void Draw(Map map, PscMapComponent psc)
        {
            if (map == null || psc == null) return;
            var links = psc.Links;
            if (links.IsEmpty) return;
            if (map != Find.CurrentMap) return;
            if (!TryGetSelectedUnit(map, out PscHaulUnit selected)) return;   // only while a storage is selected

            BuildIdMap(map);
            centerCache.Clear();
            string selId = selected.UniqueLoadID;

            var list = links.Links;
            for (int i = 0; i < list.Count; i++)
            {
                var l = list[i];
                if (!ShowAll && l.sourceId != selId && l.destId != selId) continue;
                if (!idMap.TryGetValue(l.sourceId, out var su) || !idMap.TryGetValue(l.destId, out var du)) continue;
                if (su.Settings == null || du.Settings == null) continue;
                if (!TryCenter(l.sourceId, su, out var a) || !TryCenter(l.destId, du, out var b)) continue;

                bool valid = psc.HasFunctionalFeederEdge(su, du);
                DrawLink(a, b, valid);
            }
        }

        private static bool TryGetSelectedUnit(Map map, out PscHaulUnit unit)
        {
            unit = default;
            var sel = Find.Selector;
            if (sel.SelectedZone is Zone_Stockpile zs && zs.Map == map)
            {
                unit = PscHaulUnit.ResolveSettings(zs.GetStoreSettings());
                return unit.IsValid;
            }
            if (sel.SingleSelectedThing is Building_Storage bs && bs.Map == map)
            {
                unit = PscHaulUnit.ResolveSettings(bs.GetStoreSettings());
                return unit.IsValid;
            }
            return false;
        }

        private static void BuildIdMap(Map map)
        {
            idMap.Clear();
            var groups = map.haulDestinationManager.AllGroupsListForReading;
            for (int i = 0; i < groups.Count; i++)
            {
                var u = PscHaulUnit.FromSlotGroup(groups[i]);
                if (!u.IsValid) continue;
                var id = u.UniqueLoadID;
                if (id != null) idMap[id] = u;
            }
        }

        // Draw center for an endpoint, memoized per Draw() so a unit shared by many links computes
        // its centroid once. The id is the key the draw loop already holds.
        private static bool TryCenter(string id, PscHaulUnit u, out Vector3 center)
        {
            if (centerCache.TryGetValue(id, out var cached))
            {
                center = cached ?? default;
                return cached.HasValue;
            }
            bool ok = u.TryGetDrawCenter(out center);
            centerCache[id] = ok ? center : (Vector3?)null;
            return ok;
        }

        // ---- primitives (Contagion pattern) ----

        private static void DrawLink(Vector3 a, Vector3 b, bool valid)
        {
            float y = AltitudeLayer.MetaOverlays.AltitudeFor() + 0.1f;
            a.y = y; b.y = y;
            Material mat = GetMat(valid ? GoodColor : BadColor);
            GenDraw.DrawLineBetween(a, b, mat, 0.06f);
            DrawArrows(a, b, mat);
            if (!valid) DrawCrosses(a, b, mat);
        }

        private static void DrawArrows(Vector3 origin, Vector3 end, Material mat)
        {
            Vector3 dir = end - origin; dir.y = 0f;
            float len = dir.magnitude;
            if (len < 0.0001f) return;
            dir /= len;
            const float endOffset = 1f;
            if (len < 2.4f * endOffset) { DrawArrowHead(Vector3.Lerp(origin, end, 0.5f), dir, mat); return; }
            DrawArrowHead(origin + dir * endOffset, dir, mat);
            DrawArrowHead(end - dir * endOffset, dir, mat);
        }

        private static void DrawArrowHead(Vector3 tip, Vector3 dir, Material mat)
        {
            const float arm = 0.28f, wid = 0.2f;
            Vector3 perp = new Vector3(-dir.z, 0f, dir.x);
            Vector3 back = tip - dir * arm;
            GenDraw.DrawLineBetween(tip, back + perp * wid, mat, 0.04f);
            GenDraw.DrawLineBetween(tip, back - perp * wid, mat, 0.04f);
        }

        private static void DrawCrosses(Vector3 origin, Vector3 end, Material mat)
        {
            Vector3 dir = end - origin; dir.y = 0f;
            float len = dir.magnitude;
            if (len < 0.5f) return;
            dir /= len;
            Vector3 perp = new Vector3(-dir.z, 0f, dir.x);
            int n = Mathf.Clamp(Mathf.RoundToInt(len / 2f), 1, 6);
            const float r = 0.18f;
            Vector3 d1 = (dir + perp) * r;
            Vector3 d2 = (dir - perp) * r;
            for (int i = 1; i <= n; i++)
            {
                Vector3 c = Vector3.Lerp(origin, end, i / (float)(n + 1));
                GenDraw.DrawLineBetween(c - d1, c + d1, mat, 0.05f);
                GenDraw.DrawLineBetween(c - d2, c + d2, mat, 0.05f);
            }
        }

        private static Material GetMat(Color c)
        {
            int key = (Mathf.RoundToInt(c.r * 255f) << 24) | (Mathf.RoundToInt(c.g * 255f) << 16)
                      | (Mathf.RoundToInt(c.b * 255f) << 8) | Mathf.RoundToInt(c.a * 255f);
            if (!MatCache.TryGetValue(key, out var m))
            {
                m = MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.MetaOverlay, c);
                m.enableInstancing = true;
                MatCache[key] = m;
            }
            return m;
        }
    }
}
