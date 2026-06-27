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
    // Visual language (all toggle-gated, see PscSettings):
    //   colour  — invalid = red (+ ×); valid incoming = green; valid outgoing = amber (directionColor).
    //             Outgoing/incoming is relative to the FOCUS: a route leaving the focused chain is
    //             "outgoing" (amber), one arriving is "incoming" (green).
    //   focus   — the focus is a SET: every selected storage, plus the one under the cursor (added,
    //             not replacing). Multi-select shows all their routes (item 1); hover never steals
    //             focus from the selection (item 3).
    //   chain   — from the focus we BFS the whole up/downstream chain (chainHighlight). Opacity fades
    //             geometrically per hop and floors at the background level, so a deep chain settles
    //             into the background instead of vanishing (item 4).
    //   shading — hashShading nudges each line's value (lightness) by a smooth, quantized field of its
    //             midpoint + angle, so a dense bundle separates while staying clearly in its colour band (item 6).
    //   dots    — dotsOnly replaces arrows with flow dots on every valid route (item 2).
    [StaticConstructorOnStartup]
    public static class PscFeederOverlay
    {
        // Base hues (alpha is computed per link from the chain tier, then baked in via GetMat).
        private static readonly Color GreenRGB = new Color(0.30f, 0.92f, 0.34f, 1f);  // valid incoming
        private static readonly Color AmberRGB = new Color(0.98f, 0.74f, 0.20f, 1f);  // valid outgoing
        private static readonly Color RedRGB   = new Color(0.92f, 0.22f, 0.22f, 1f);  // invalid (either way)
        private static readonly Dictionary<int, Material> MatCache = new Dictionary<int, Material>();

        // ---- line + flow-dot tuning ----
        private const float DefaultLineWidth = 0.04f;   // shipped line thickness; tunable via the dev slider
        private const float ArrowWidthFrac = 0.667f;    // arrowhead stroke as a fraction of the line width
        private const float CrossWidthFrac = 0.833f;    // ✕ stroke as a fraction of the line width
        private const float DotSpeed = 2f;              // bead travel speed (cells/sec, real time) — uniform across routes
        private const float DotSpacing = 3f;            // target gap between beads (cells); bead COUNT scales to hold this
        private const int DotCountMax = 24;             // cap beads on very long routes (perf + clutter)
        private const float DotScale = 0.15f;           // bead diameter (cells)

        // ---- opacity grading ----
        private const float BrightAlpha = 0.88f;        // a tier-1 (focus-incident) route
        private const float Falloff = 0.6f;             // per-hop opacity multiplier down/up the chain
        private const float FloorAlpha = 0.13f;         // deep-chain floor; ≈ the background level it settles into
        private const float NeutralAlpha = 0.45f;       // overlay-on, nothing focused (or focus-dim off)
        private const float BackgroundAlpha = 0.12f;    // overlay-on, focus-dim on: routes off the focused chain
        private const float AnchorAlphaFrac = 0.4f;     // faint static chevron drawn under flow dots

        // ---- hash shading ----
        // Two independent banded fields (value + hue) so routes separate as a 2-D shade space, not a
        // 1-D gradient. The base hues are already near-max brightness, so the value shift runs mostly
        // DOWNWARD (into the available headroom) for a visible spread; the hue shift is small and wraps,
        // so the colour never leaves its red/green/amber band.
        private const int HashValueBuckets = 6;         // lightness levels
        private const float HashValueLo = -0.34f;       // darkest value offset (uses the headroom below the bright base)
        private const float HashValueHi = 0.08f;        // lightest value offset
        private const int HashHueBuckets = 5;           // hue levels
        private const float HashHueAmp = 0.05f;         // max hue shift in HSV units (0..1) ≈ ±18°, stays in band

        // Round, double-sided dot mesh for the flow beads (built once, lazily). Round = direction-
        // agnostic (unlike the old square quad); double-sided so it shows regardless of cull mode.
        private static Mesh dotMesh;

        // Feathered line texture (alpha ramps 0→1→0 across the width axis) so the line/arrow/✕ quads
        // get soft, anti-aliased edges instead of hard stair-steps. Built once, lazily.
        private static Texture2D lineTex;

        // Per-frame snapshot of the render settings, read by the primitives without extra args.
        private static bool sFlowDots;
        private static bool sDotsOnly;
        private static float sLineWidth = DefaultLineWidth;

        // Reused per-frame scratch so the draw path allocates nothing steady-state.
        private static readonly Dictionary<string, PscHaulUnit> idMap = new Dictionary<string, PscHaulUnit>();
        private static readonly HashSet<string> focusSeeds = new HashSet<string>();
        private static readonly Dictionary<string, int> downDist = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> upDist = new Dictionary<string, int>();
        private static readonly Queue<string> bfsQueue = new Queue<string>();

        // Chain-BFS memo: the distances depend only on the focus seeds and the graph generation, so
        // recompute only when one of those changes (the result is reused across frames otherwise).
        private static long lastChainSig = long.MinValue;
        private static int lastChainGen = -1;

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
            bool chain = settings == null || settings.feederChainHighlight;
            bool directionColor = settings == null || settings.feederDirectionColor;
            bool hashShade = settings == null || settings.feederHashShading;
            sFlowDots = settings != null && settings.feederFlowDots;
            sDotsOnly = sFlowDots && settings.feederDotsOnly;   // dots-only is a sub-mode of flow dots
            sLineWidth = Mathf.Clamp(settings?.feederLineWidth ?? DefaultLineWidth, 0.02f, 0.2f);

            // Focus seeds: every selected storage (item 1), plus the one under the cursor — ADDED,
            // never replacing the selection (item 3). Overlay off needs at least one SELECTED unit
            // to show anything; overlay on highlights on hover alone.
            bool overlay = PscOverlayState.Active;
            focusSeeds.Clear();
            GatherSelectedIds(map, focusSeeds);
            if (!overlay && focusSeeds.Count == 0) return;
            string hovered = ResolveHoveredId(map);
            if (hovered != null) focusSeeds.Add(hovered);
            bool hasFocus = focusSeeds.Count > 0;

            // Chain distances from the focus (cached on seeds + graph generation).
            int gen = links.Generation;
            long sig = SeedSignature(focusSeeds);
            if (sig != lastChainSig || gen != lastChainGen)
            {
                links.ComputeChainDistances(focusSeeds, downDist, upDist, bfsQueue);
                lastChainSig = sig;
                lastChainGen = gen;
            }

            BuildIdMap(map);
            centerCache.Clear();
            if (portSpreading) PscFeederLayout.EnsureBuilt(map, psc);

            // Off the chain, the background level depends on focus-dim — but only when there IS a
            // focus to contrast against; with nothing focused, everything reads at the neutral level.
            float backgroundAlpha = (hasFocus && focusDim) ? BackgroundAlpha : NeutralAlpha;

            var list = links.Links;
            for (int i = 0; i < list.Count; i++)
            {
                var l = list[i];
                if (!idMap.TryGetValue(l.sourceId, out var su) || !idMap.TryGetValue(l.destId, out var du)) continue;
                if (su.Settings == null || du.Settings == null) continue;

                // Tier + direction from the chain BFS. tier == 0 means "not on the focused chain".
                int tier = ClassifyLink(l, chain, out bool outgoing);
                if (!overlay && tier == 0) continue;                // overlay off: only the focused chain draws

                Vector3 a, b;
                if (!portSpreading || !PscFeederLayout.TryGetPorts(i, out a, out b))
                {
                    // Spreading off, or no port for this route (unresolved/degenerate): centroid fallback.
                    if (!TryCenter(l.sourceId, su, out a) || !TryCenter(l.destId, du, out b)) continue;
                }

                bool valid = psc.HasFunctionalFeederEdge(su, du);
                float alpha = tier > 0 ? TierAlpha(tier) : backgroundAlpha;

                Color baseRGB = !valid ? RedRGB : (outgoing && directionColor ? AmberRGB : GreenRGB);
                if (hashShade) baseRGB = ApplyHashShade(baseRGB, a, b);
                baseRGB.a = alpha;

                // Animate this route? dotsOnly animates every valid route; otherwise flow dots are the
                // focus-incident (tier-1) treatment only, kept from drowning deeper chain links in motion.
                bool animate = valid && (sDotsOnly || (sFlowDots && tier == 1));
                bool anchor = animate && !sDotsOnly;               // faint static chevron under the dots
                bool arrows = !animate && !sDotsOnly;              // arrows everywhere except dotsOnly mode

                DrawLink(a, b, baseRGB, valid, animate, anchor, arrows);
            }
        }

        // Hop distance + direction of a link relative to the focus chain. Returns 0 when the link is
        // not on the chain. tier 1 = a focus-incident route. With chainHighlight off, only the seeds
        // themselves anchor (dist 0), so the highlight stays at the focus's own direct routes.
        private static int ClassifyLink(PscFeederLink l, bool chain, out bool outgoing)
        {
            outgoing = false;
            int tier = int.MaxValue;
            // Downstream: a route leaving a chain-reached source continues the flow away from focus → amber.
            if (downDist.TryGetValue(l.sourceId, out int ds) && (chain || ds == 0))
            {
                tier = ds + 1;
                outgoing = true;
            }
            // Upstream: a route arriving at a chain-reached dest feeds toward focus → green. Tie → outgoing.
            if (upDist.TryGetValue(l.destId, out int us) && (chain || us == 0))
            {
                int t = us + 1;
                if (t < tier) { tier = t; outgoing = false; }
            }
            return tier == int.MaxValue ? 0 : tier;
        }

        private static float TierAlpha(int tier)
        {
            float a = BrightAlpha * Mathf.Pow(Falloff, tier - 1);
            return a < FloorAlpha ? FloorAlpha : a;
        }

        // Order-independent signature of the focus-seed set, combined with the graph generation, so
        // the chain BFS recomputes only when the focus or the link set actually changes.
        private static long SeedSignature(HashSet<string> seeds)
        {
            int xor = 0, sum = 0;
            foreach (var s in seeds)
            {
                int h = s.GetHashCode();
                xor ^= h;
                sum = unchecked(sum + h);
            }
            return unchecked(((long)xor << 32) ^ (uint)sum ^ ((long)seeds.Count * 2654435761L));
        }

        private static void GatherSelectedIds(Map map, HashSet<string> into)
        {
            var sel = Find.Selector.SelectedObjectsListForReading;
            for (int i = 0; i < sel.Count; i++)
            {
                PscHaulUnit u = default;
                if (sel[i] is Zone_Stockpile zs && zs.Map == map)
                    u = PscHaulUnit.ResolveSettings(zs.GetStoreSettings());
                else if (sel[i] is Building_Storage bs && bs.Map == map)
                    u = PscHaulUnit.ResolveSettings(bs.GetStoreSettings());
                if (!u.IsValid) continue;
                var id = u.UniqueLoadID;
                if (id != null) into.Add(id);
            }
        }

        // The storage unit under the mouse, or null. Added to the focus set (never replaces it).
        private static string ResolveHoveredId(Map map)
        {
            IntVec3 cell = UI.MouseCell();
            if (!cell.InBounds(map)) return null;
            var hovered = PscHaulUnit.ResolveCell(cell, map);
            return hovered.IsValid ? hovered.UniqueLoadID : null;
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

        private static void DrawLink(Vector3 a, Vector3 b, Color color, bool valid, bool animate, bool anchor, bool arrows)
        {
            float y = AltitudeLayer.MetaOverlays.AltitudeFor() + 0.1f;
            a.y = y; b.y = y;
            Material mat = GetMat(color);
            GenDraw.DrawLineBetween(a, b, mat, sLineWidth);

            // Flow dots AUGMENT a static anchor (Council: never rely on motion alone) when not in
            // dots-only mode — a faint chevron carries direction, bright dots carry flow.
            if (anchor)
            {
                Color faint = color; faint.a = color.a * AnchorAlphaFrac;
                DrawArrows(a, b, GetMat(faint));
            }
            if (arrows) DrawArrows(a, b, mat);
            if (animate) DrawFlowDots(a, b, mat);
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

        // Round beads flowing source→dest along the route, in the route's own colour. The bead COUNT
        // scales with length to hold ~DotSpacing cells between beads (a uniform conveyor across routes),
        // but never drops below 1 — even stockpiles a single cell apart keep a bead riding the line.
        // All n beads are always on the line (t wraps within [0, 1)), so there is no frame with zero
        // beads: the player never waits to see one. Speed is DotSpeed cells/sec regardless of length, so
        // only the count differs between routes, not the speed. Real-time phase → beads move while paused.
        private static void DrawFlowDots(Vector3 a, Vector3 b, Material mat)
        {
            float y = a.y;
            float len = (new Vector3(b.x - a.x, 0f, b.z - a.z)).magnitude;
            int n = Mathf.Clamp(Mathf.CeilToInt(len / DotSpacing), 1, DotCountMax);
            float cycle = Time.realtimeSinceStartup * DotSpeed / Mathf.Max(len, 0.5f);
            Mesh mesh = DotMesh();
            for (int k = 0; k < n; k++)
            {
                float t = cycle + k / (float)n;
                t -= Mathf.Floor(t);
                Vector3 p = Vector3.Lerp(a, b, t); p.y = y;
                Graphics.DrawMesh(mesh,
                    Matrix4x4.TRS(p, Quaternion.identity, new Vector3(DotScale, 1f, DotScale)),
                    mat, 0);
            }
        }

        // Quantized perturbation of a route's shade so a dense bundle separates. Two independent smooth
        // fields of the route's world midpoint + angle drive value (lightness) and hue separately, giving
        // a 2-D space of shades that reads far more distinctly than a single gradient. Each field is
        // banded (quantized) so the material cache stays bounded; both amounts are small and the hue wraps,
        // so the colour stays clearly in its green/amber/red band. World-space → no flicker as the camera pans.
        private static Color ApplyHashShade(Color c, Vector3 a, Vector3 b)
        {
            float cx = (a.x + b.x) * 0.5f;
            float cz = (a.z + b.z) * 0.5f;
            float ang = Mathf.Atan2(b.z - a.z, b.x - a.x);   // *2 below folds direction (period π)

            // amplitudes sum to 1 → each field in [-1, 1]; different freqs/phases keep them independent.
            float fieldV = Mathf.Sin(cx * 0.75f + cz * 0.42f) * 0.6f
                         + Mathf.Sin(cz * 0.68f - cx * 0.28f + 2.1f) * 0.25f
                         + Mathf.Cos(ang * 2f) * 0.15f;
            float fieldH = Mathf.Sin(cx * 0.46f - cz * 0.63f + 1.0f) * 0.55f
                         + Mathf.Sin(cz * 0.39f + cx * 0.26f + 4.0f) * 0.30f
                         + Mathf.Sin(ang * 2f + 0.7f) * 0.15f;

            float vLevel = QuantizeSigned(fieldV, HashValueBuckets) * 0.5f + 0.5f;   // 0..1
            float vShift = Mathf.Lerp(HashValueLo, HashValueHi, vLevel);             // asymmetric → mostly darker
            float hShift = QuantizeSigned(fieldH, HashHueBuckets) * HashHueAmp;

            Color.RGBToHSV(c, out float h, out float s, out float v);
            h = Mathf.Repeat(h + hShift, 1f);
            v = Mathf.Clamp01(v + vShift);
            Color outc = Color.HSVToRGB(h, s, v);
            outc.a = c.a;
            return outc;
        }

        // Map a smooth field value in [-1, 1] to one of `buckets` signed levels in [-1, 1], so distinct
        // shades stay finite (bounded material cache) while neighbouring routes still band together.
        private static float QuantizeSigned(float field, int buckets)
        {
            field = Mathf.Clamp(field, -1f, 1f);
            int b = Mathf.Clamp(Mathf.RoundToInt((field * 0.5f + 0.5f) * (buckets - 1)), 0, buckets - 1);
            return (b / (float)(buckets - 1) - 0.5f) * 2f;
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
