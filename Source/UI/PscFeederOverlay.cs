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
        // Dimmed variants for routes not touching the focused (hovered/selected) unit: same hue,
        // desaturated and low-alpha so they recede without vanishing.
        private static readonly Color GoodColorDim = new Color(0.42f, 0.70f, 0.44f, 0.20f);
        private static readonly Color BadColorDim = new Color(0.72f, 0.42f, 0.42f, 0.22f);
        private static readonly Dictionary<int, Material> MatCache = new Dictionary<int, Material>();

        // ---- line + flow-dot tuning ----
        private const float DefaultLineWidth = 0.04f;   // shipped line thickness; tunable via the dev slider
        private const float ArrowWidthFrac = 0.667f;    // arrowhead stroke as a fraction of the line width
        private const float CrossWidthFrac = 0.833f;    // ✕ stroke as a fraction of the line width
        private const int DotCount = 3;                 // beads per animated route
        private const float DotSpeed = 2f;              // bead travel speed (cells/sec, real time)
        private const float DotScale = 0.15f;           // bead diameter (cells)

        // Round, double-sided dot mesh for the flow beads (built once, lazily). Round = direction-
        // agnostic (unlike the old square quad); double-sided so it shows regardless of cull mode.
        private static Mesh dotMesh;

        // Feathered line texture (alpha ramps 0→1→0 across the width axis) so the line/arrow/✕ quads
        // get soft, anti-aliased edges instead of hard stair-steps. Built once, lazily.
        private static Texture2D lineTex;

        // Per-frame snapshot of the render settings, read by the primitives without extra args.
        private static bool sFlowDots;
        private static float sLineWidth = DefaultLineWidth;

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
            if (Find.ScreenshotModeHandler.Active) return;   // parity with the panel overlay

            var settings = PscMod.Settings;
            bool portSpreading = settings == null || settings.feederPortSpreading;
            bool focusDim = settings == null || settings.feederFocusDim;
            sFlowDots = settings != null && settings.feederFlowDots;
            sLineWidth = Mathf.Clamp(settings?.feederLineWidth ?? DefaultLineWidth, 0.02f, 0.2f);

            // Overlay on -> draw every route (no selection needed). Overlay off -> legacy behaviour:
            // draw only routes incident to the selected storage (nothing if none is selected).
            bool overlay = PscOverlayState.Active;
            string selId = null;
            if (!overlay)
            {
                if (!TryGetSelectedUnit(map, out PscHaulUnit selected)) return;
                selId = selected.UniqueLoadID;
            }

            BuildIdMap(map);
            centerCache.Clear();
            if (portSpreading) PscFeederLayout.EnsureBuilt(map, psc);

            // Focus target for dimming (overlay-all mode only — overlay-off already shows just the
            // selected unit's routes). The unit under the cursor, else the selected one; null => no
            // dimming. One cursor->cell resolve per frame, no iteration.
            string focusId = (overlay && focusDim) ? ResolveFocusId(map) : null;

            var list = links.Links;
            for (int i = 0; i < list.Count; i++)
            {
                var l = list[i];
                if (!overlay && l.sourceId != selId && l.destId != selId) continue;
                if (!idMap.TryGetValue(l.sourceId, out var su) || !idMap.TryGetValue(l.destId, out var du)) continue;
                if (su.Settings == null || du.Settings == null) continue;

                Vector3 a, b;
                if (!portSpreading || !PscFeederLayout.TryGetPorts(i, out a, out b))
                {
                    // Spreading off, or no port for this route (unresolved/degenerate): centroid fallback.
                    if (!TryCenter(l.sourceId, su, out a) || !TryCenter(l.destId, du, out b)) continue;
                }

                bool valid = psc.HasFunctionalFeederEdge(su, du);
                bool incident = focusId == null || l.sourceId == focusId || l.destId == focusId;
                bool focused = focusId != null && (l.sourceId == focusId || l.destId == focusId);
                DrawLink(a, b, valid, incident, focused);
            }
        }

        // The unit under the mouse, falling back to the selected unit. Used only for focus/dim.
        private static string ResolveFocusId(Map map)
        {
            IntVec3 cell = UI.MouseCell();
            if (cell.InBounds(map))
            {
                var hovered = PscHaulUnit.ResolveCell(cell, map);
                if (hovered.IsValid)
                {
                    var id = hovered.UniqueLoadID;
                    if (id != null) return id;
                }
            }
            return TryGetSelectedUnit(map, out var sel) ? sel.UniqueLoadID : null;
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

        private static void DrawLink(Vector3 a, Vector3 b, bool valid, bool incident, bool focused)
        {
            float y = AltitudeLayer.MetaOverlays.AltitudeFor() + 0.1f;
            a.y = y; b.y = y;
            Color color = valid
                ? (incident ? GoodColor : GoodColorDim)
                : (incident ? BadColor : BadColorDim);
            Material mat = GetMat(color);
            GenDraw.DrawLineBetween(a, b, mat, sLineWidth);

            // Flow dots AUGMENT the chevron (Council: never rely on motion alone): on a focused valid
            // route, draw a dimmed chevron as a static direction anchor, then bright moving dots over it.
            bool dots = sFlowDots && valid && focused;
            DrawArrows(a, b, dots ? GetMat(GoodColorDim) : mat);
            if (dots) DrawFlowDots(a, b);
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
            GenDraw.DrawLineBetween(tip, back + perp * wid, mat, sLineWidth * ArrowWidthFrac);
            GenDraw.DrawLineBetween(tip, back - perp * wid, mat, sLineWidth * ArrowWidthFrac);
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
                GenDraw.DrawLineBetween(c - d1, c + d1, mat, sLineWidth * CrossWidthFrac);
                GenDraw.DrawLineBetween(c - d2, c + d2, mat, sLineWidth * CrossWidthFrac);
            }
        }

        // Round beads flowing source→dest along the route. Real-time phase so they keep moving while the
        // game is paused (the faint chevron under them carries direction regardless). DotSpeed is in
        // cells/sec, so visual speed is roughly route-length-independent.
        private static void DrawFlowDots(Vector3 a, Vector3 b)
        {
            float y = a.y;
            float len = (new Vector3(b.x - a.x, 0f, b.z - a.z)).magnitude;
            float cycle = Time.realtimeSinceStartup * DotSpeed / Mathf.Max(len, 0.5f);
            Material mat = GetMat(GoodColor);
            Mesh mesh = DotMesh();
            for (int k = 0; k < DotCount; k++)
            {
                float t = cycle + k / (float)DotCount;
                t -= Mathf.Floor(t);
                Vector3 p = Vector3.Lerp(a, b, t); p.y = y;
                Graphics.DrawMesh(mesh,
                    Matrix4x4.TRS(p, Quaternion.identity, new Vector3(DotScale, 1f, DotScale)),
                    mat, 0);
            }
        }

        // A unit-diameter filled circle (radius 0.5) as a triangle fan, doubled so both faces render
        // regardless of the shader's cull mode. Every vertex UV is (0.5, 0.5) — the feathered line
        // texture's opaque centre — so the dot stays a solid colour disc (its roundness is geometry).
        private static Mesh DotMesh()
        {
            if (dotMesh != null) return dotMesh;
            const int seg = 16;
            var verts = new Vector3[seg + 1];
            var uvs = new Vector2[seg + 1];
            verts[0] = Vector3.zero;
            for (int i = 0; i < seg; i++)
            {
                float ang = i / (float)seg * Mathf.PI * 2f;
                verts[i + 1] = new Vector3(Mathf.Cos(ang) * 0.5f, 0f, Mathf.Sin(ang) * 0.5f);
            }
            for (int i = 0; i < uvs.Length; i++) uvs[i] = new Vector2(0.5f, 0.5f);
            var tris = new int[seg * 6];   // front + back faces
            int t = 0;
            for (int i = 0; i < seg; i++)
            {
                int v1 = i + 1, v2 = (i + 1) % seg + 1;
                tris[t++] = 0; tris[t++] = v1; tris[t++] = v2;   // front
                tris[t++] = 0; tris[t++] = v2; tris[t++] = v1;   // back
            }
            dotMesh = new Mesh { name = "PscFeederDot", vertices = verts, uv = uvs, triangles = tris };
            dotMesh.RecalculateBounds();
            return dotMesh;
        }

        // 32×1 texture with alpha opaque in the centre, feathering to 0 at the two width edges. plane10
        // maps U to the line's width axis, and MetaOverlay multiplies texture alpha by colour alpha, so
        // this softens the long edges of every line/arrow/✕ → anti-aliased without a custom shader.
        private static Texture2D LineTex()
        {
            if (lineTex != null) return lineTex;
            const int w = 32;
            var tex = new Texture2D(w, 1, TextureFormat.ARGB32, false)
            { name = "PscFeederLine", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w;                       // 0..1 across the width
                float d = Mathf.Abs(u * 2f - 1f);               // 0 centre .. 1 edge
                float a = Mathf.Clamp01((1f - d) / 0.25f);      // opaque centre, ~12.5% feather each edge
                tex.SetPixel(x, 0, new Color(1f, 1f, 1f, a));
            }
            tex.Apply();
            lineTex = tex;
            return lineTex;
        }

        private static Material GetMat(Color c)
        {
            int key = (Mathf.RoundToInt(c.r * 255f) << 24) | (Mathf.RoundToInt(c.g * 255f) << 16)
                      | (Mathf.RoundToInt(c.b * 255f) << 8) | Mathf.RoundToInt(c.a * 255f);
            if (!MatCache.TryGetValue(key, out var m))
            {
                m = MaterialPool.MatFrom(LineTex(), ShaderDatabase.MetaOverlay, c);
                m.enableInstancing = true;
                MatCache[key] = m;
            }
            return m;
        }
    }
}
