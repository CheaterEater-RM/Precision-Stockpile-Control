using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // The on-map storage overlay's screen-space panels, drawn from PscMapComponent.MapComponentOnGUI
    // when PscOverlayState.Active. A compact panel floats above every storage unit showing its active
    // policy icons (PscStatusIcons) plus a priority readout (band-coloured dot + level/initial +
    // letter). Untouched storage shows priority only — the draw path reads with TryGet and NEVER
    // creates PscStorageData.
    //
    // Panels are zoom-gated (the route lines in PscFeederOverlay are not). Drawing is zero-alloc in
    // steady state: a grow-only Panel pool, a reused dedupe set, cached glyph strings, and tooltips
    // built only on hover. Overlapping panels are nudged downward (greedy, capped passes).
    public static class PscStorageOverlay
    {
        // ---- layout ----
        private const float IconSize = 18f;
        private const float IconGap = 2f;
        private const float TextGap = 5f;       // between the icon run and the priority readout
        private const float DotSize = 8f;
        private const float DotTextGap = 3f;    // between the band dot and the level glyph
        private const float GlyphGap = 3f;      // between the level/initial and the letter
        private const float RowH = 18f;
        private const float Pad = 4f;           // configured panels
        private const float LitePad = 2f;       // priority-only panels (lighter)
        private const float LiftGap = 4f;       // how far above the anchor the box floats
        private const float DeclutterGap = 2f;
        private const int DeclutterPasses = 8;

        // ---- colours ----
        private static readonly Color Bg = new Color(0f, 0f, 0f, 0.62f);
        private static readonly Color LiteBg = new Color(0f, 0f, 0f, 0.42f);
        private static readonly Color BorderColor = new Color(1f, 1f, 1f, 0.22f);
        private static readonly Color LevelColor = new Color(0.92f, 0.92f, 0.92f);
        private static readonly Color LetterColor = new Color(0.70f, 0.70f, 0.70f, 0.95f);

        // Fixed draw order for the active-icon run.
        private static readonly PscStatusIcons.Flag[] IconOrder =
        {
            PscStatusIcons.Flag.Mode, PscStatusIcons.Flag.Limits,
            PscStatusIcons.Flag.BatchFill, PscStatusIcons.Flag.BatchEmpty,
            PscStatusIcons.Flag.OnlyFrom, PscStatusIcons.Flag.OnlyTo,
            PscStatusIcons.Flag.Alarm
        };

        // Cached glyph strings (no per-frame string alloc). Levels indexed 1..10; letters 0..25.
        private static readonly string[] LevelGlyphs = BuildLevels();
        private static readonly string[] LetterGlyphs = BuildLetters();

        private static string[] BuildLevels()
        {
            var a = new string[11];
            for (int i = 1; i <= 10; i++) a[i] = i.ToString();
            return a;
        }

        private static string[] BuildLetters()
        {
            var a = new string[26];
            for (int i = 0; i < 26; i++) a[i] = ((char)('a' + i)).ToString();
            return a;
        }

        // One panel per visible unit. Mutable reference type, pooled and reused across frames, so the
        // declutter pass can move rects in place and nothing allocates per panel.
        private sealed class Panel
        {
            public Rect rect;
            public PscStatusIcons.Flag flags;
            public PscStorageMode mode;
            public int iconCount;
            public Color band;
            public string levelGlyph;
            public string letterGlyph;
            public float levelW;
            public float letterW;
            public float pad;
            public bool lite;
            public int stableId;         // tie-break key for stable declutter ordering
            public PscHaulUnit unit;     // for the hover tooltip
            public PscStorageData data;  // may be null (untouched storage)
        }

        private static readonly List<Panel> pool = new List<Panel>();
        private static int active;
        private static readonly HashSet<PscHaulUnit> seen = new HashSet<PscHaulUnit>();
        private static readonly IntVec3[] candidates = new IntVec3[5];

        public static void Draw(Map map)
        {
            if (!PscOverlayState.Active) return;                  // off ⇒ pays nothing
            if (map == null || map != Find.CurrentMap) return;
            if (Find.ScreenshotModeHandler.Active) return;
            if (Find.CameraDriver.CurrentZoom > CameraZoomRange.Close) return;   // panels zoom-gated

            bool numbering = PscMod.Settings != null && PscMod.Settings.priorityNumbering;
            bool letters = PscMod.Settings != null && PscMod.Settings.subpriorityLetters;

            Text.Font = GameFont.Tiny;                           // set BEFORE measuring (CalcSize)
            active = 0;
            seen.Clear();

            var groups = map.haulDestinationManager.AllGroupsListForReading;
            for (int i = 0; i < groups.Count; i++)
            {
                var u = PscHaulUnit.FromSlotGroup(groups[i]);
                if (!u.IsValid || !seen.Add(u)) continue;        // dedupe linked StorageGroups
                var settings = u.Settings;
                if (settings == null) continue;
                if (!TryAnchorScreenPos(u, map, out Vector2 anchor)) continue;

                var data = PscStorageDataStore.TryGet(settings);  // may be null — NEVER GetOrCreate
                var p = NextPooled();
                p.unit = u;
                p.data = data;
                p.flags = PscStatusIcons.Resolve(data);
                p.mode = data?.mode ?? PscStorageMode.Normal;
                p.band = PscOverlayState.BandColor(settings.Priority);
                p.levelGlyph = numbering
                    ? LevelGlyphs[PscOrder.DisplayLevel(PscOrder.LevelFor(settings.Priority, data?.subTier ?? 0))]
                    : PscOverlayState.BandInitial(settings.Priority);
                p.letterGlyph = letters ? LetterGlyphFor(data?.letter) : null;   // a-z off ⇒ no glyph (and the tooltip append at line ~344 drops too)
                p.stableId = u.UniqueLoadID?.GetHashCode() ?? 0;
                MeasureAndPlace(p, anchor);
            }

            if (active == 0) { ReleasePoolTail(); Text.Font = GameFont.Small; return; }

            Declutter();
            for (int i = 0; i < active; i++) DrawPanel(pool[i]);
            ReleasePoolTail();   // drop stale unit/data refs in unused tail slots so a deleted pile isn't pinned

            Text.Font = GameFont.Small;
        }

        private static string LetterGlyphFor(string letter)
        {
            if (string.IsNullOrEmpty(letter)) return null;
            char c = char.ToLowerInvariant(letter[0]);
            return (c >= 'a' && c <= 'z') ? LetterGlyphs[c - 'a'] : null;
        }

        private static Panel NextPooled()
        {
            Panel p;
            if (active < pool.Count) p = pool[active];
            else { p = new Panel(); pool.Add(p); }
            active++;
            return p;
        }

        // Null the unit/data refs in pool slots beyond `active` so an unused panel (after a pile is
        // deleted or fewer are visible this frame) doesn't pin a dead SlotGroup/StorageGroup alive
        // until that slot is next reused. Bounded by peak panel count; only ever iterated [0, active).
        private static void ReleasePoolTail()
        {
            for (int i = active; i < pool.Count; i++)
            {
                pool[i].unit = default;
                pool[i].data = null;
            }
        }

        // Pick the screen position to anchor the panel above. Works in screen space (no world-axis
        // assumption): project a few candidate cells through GenMapUI.LabelDrawPosFor and take the one
        // with the smallest screen y (highest on screen), tie-broken nearest screen-centre x. CellsList
        // is a static temporary — consumed immediately, never retained.
        private static bool TryAnchorScreenPos(PscHaulUnit u, Map map, out Vector2 anchor)
        {
            anchor = default;
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
            int midX = (minX + maxX) / 2, midZ = (minZ + maxZ) / 2;

            // Skip undiscovered storage (centre cell fogged).
            if (new IntVec3(midX, 0, midZ).Fogged(map)) return false;

            candidates[0] = new IntVec3(midX, 0, maxZ);   // north edge mid
            candidates[1] = new IntVec3(midX, 0, minZ);   // south edge mid
            candidates[2] = new IntVec3(maxX, 0, midZ);   // east edge mid
            candidates[3] = new IntVec3(minX, 0, midZ);   // west edge mid
            candidates[4] = new IntVec3(midX, 0, midZ);   // centroid

            float screenCenterX = UI.screenWidth / 2f;
            bool have = false;
            float bestY = 0f, bestDx = 0f;
            for (int i = 0; i < candidates.Length; i++)
            {
                Vector2 sp = GenMapUI.LabelDrawPosFor(candidates[i]);
                float dx = Mathf.Abs(sp.x - screenCenterX);
                if (!have || sp.y < bestY - 0.01f || (sp.y <= bestY + 0.01f && dx < bestDx))
                {
                    have = true; bestY = sp.y; bestDx = dx; anchor = sp;
                }
            }
            return have;
        }

        private static void MeasureAndPlace(Panel p, Vector2 anchor)
        {
            int count = 0;
            for (int k = 0; k < IconOrder.Length; k++)
                if ((p.flags & IconOrder[k]) != 0) count++;
            p.iconCount = count;

            float iconRunW = count > 0 ? count * IconSize + (count - 1) * IconGap : 0f;
            p.levelW = Text.CalcSize(p.levelGlyph).x;
            p.letterW = p.letterGlyph != null ? Text.CalcSize(p.letterGlyph).x : 0f;
            float priW = DotSize + DotTextGap + p.levelW
                         + (p.letterGlyph != null ? GlyphGap + p.letterW : 0f);

            p.lite = p.flags == PscStatusIcons.Flag.None;
            p.pad = p.lite ? LitePad : Pad;
            float contentW = iconRunW + (count > 0 ? TextGap : 0f) + priW;
            float boxW = contentW + 2f * p.pad;
            float boxH = RowH + 2f * p.pad;
            p.rect = new Rect(anchor.x - boxW / 2f, anchor.y - boxH - LiftGap, boxW, boxH);
        }

        // Greedy declutter: sort the active slice by (y, x, stableId), then push any panel overlapping
        // an earlier one down below it. Capped passes guarantee termination; residual overlap at
        // extreme density is acceptable.
        private static void Declutter()
        {
            pool.Sort(0, active, PanelComparer.Instance);
            for (int pass = 0; pass < DeclutterPasses; pass++)
            {
                bool moved = false;
                for (int i = 1; i < active; i++)
                {
                    var pi = pool[i];
                    for (int j = 0; j < i; j++)
                    {
                        var pj = pool[j];
                        if (!pi.rect.Overlaps(pj.rect)) continue;
                        float ny = pj.rect.yMax + DeclutterGap;
                        if (ny > pi.rect.y)
                        {
                            var r = pi.rect; r.y = ny; pi.rect = r;
                            moved = true;
                        }
                    }
                }
                if (!moved) break;
            }
        }

        private sealed class PanelComparer : IComparer<Panel>
        {
            public static readonly PanelComparer Instance = new PanelComparer();
            public int Compare(Panel a, Panel b)
            {
                int c = a.rect.y.CompareTo(b.rect.y);
                if (c != 0) return c;
                c = a.rect.x.CompareTo(b.rect.x);
                if (c != 0) return c;
                return a.stableId.CompareTo(b.stableId);
            }
        }

        private static void DrawPanel(Panel p)
        {
            Widgets.DrawBoxSolid(p.rect, p.lite ? LiteBg : Bg);
            if (!p.lite)
            {
                var prev = GUI.color;
                GUI.color = BorderColor;
                Widgets.DrawBox(p.rect, 1);
                GUI.color = prev;
            }

            float x = p.rect.x + p.pad;
            float midY = p.rect.y + p.rect.height / 2f;

            for (int k = 0; k < IconOrder.Length; k++)
            {
                var flag = IconOrder[k];
                if ((p.flags & flag) == 0) continue;
                var tex = flag == PscStatusIcons.Flag.Mode
                    ? PscStatusIcons.ModeTex(p.mode)
                    : PscStatusIcons.TextureFor(flag);
                GUI.DrawTexture(new Rect(x, midY - IconSize / 2f, IconSize, IconSize), tex, ScaleMode.ScaleToFit);
                x += IconSize + IconGap;
            }
            if (p.iconCount > 0) x += TextGap - IconGap;   // convert trailing icon gap into the text gap

            Widgets.DrawBoxSolid(new Rect(x, midY - DotSize / 2f, DotSize, DotSize), p.band);
            x += DotSize + DotTextGap;

            var prevAnchor = Text.Anchor;
            var prevColor = GUI.color;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = LevelColor;
            Widgets.Label(new Rect(x, p.rect.y, p.levelW + 2f, p.rect.height), p.levelGlyph);
            x += p.levelW;
            if (p.letterGlyph != null)
            {
                x += GlyphGap;
                GUI.color = LetterColor;
                Widgets.Label(new Rect(x, p.rect.y, p.letterW + 2f, p.rect.height), p.letterGlyph);
            }
            GUI.color = prevColor;
            Text.Anchor = prevAnchor;

            if (Mouse.IsOver(p.rect))
                TooltipHandler.TipRegion(p.rect, BuildTooltip(p));   // built only on hover
        }

        private static readonly StringBuilder tipSb = new StringBuilder();

        private static string BuildTooltip(Panel p)
        {
            tipSb.Length = 0;
            var settings = p.unit.Settings;
            string label = p.unit.Label;
            if (!string.IsNullOrEmpty(label)) tipSb.Append(label).Append('\n');

            if (settings != null)
            {
                if (PscMod.Settings != null && PscMod.Settings.priorityNumbering)
                {
                    int level = PscOrder.LevelFor(settings.Priority, p.data?.subTier ?? 0);
                    tipSb.Append("PSC_LevelShort".Translate(PscOrder.DisplayLevel(level)).ToString());
                    tipSb.Append(" (").Append(settings.Priority.Label().CapitalizeFirst()).Append(')');
                }
                else
                {
                    tipSb.Append(settings.Priority.Label().CapitalizeFirst());
                }
            }
            if (p.letterGlyph != null) tipSb.Append(' ').Append(p.letterGlyph);

            AppendFlag(p, PscStatusIcons.Flag.Mode, ("PSC_Mode_" + p.mode).Translate());
            AppendFlag(p, PscStatusIcons.Flag.Limits, "PSC_OverlayLimits".Translate());
            AppendFlag(p, PscStatusIcons.Flag.BatchFill, "PSC_BatchFill".Translate());
            AppendFlag(p, PscStatusIcons.Flag.BatchEmpty, "PSC_BatchEmpty".Translate());
            AppendFlag(p, PscStatusIcons.Flag.OnlyFrom, "PSC_OnlyFromSource".Translate());
            AppendFlag(p, PscStatusIcons.Flag.OnlyTo, "PSC_OnlyToDestinations".Translate());
            AppendFlag(p, PscStatusIcons.Flag.Alarm, "PSC_Alarm_GizmoLabel".Translate());

            return tipSb.ToString();
        }

        private static void AppendFlag(Panel p, PscStatusIcons.Flag flag, string label)
        {
            if ((p.flags & flag) == 0) return;
            tipSb.Append('\n').Append("• ").Append(label);
        }
    }
}
