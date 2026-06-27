using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Editor for one limit group: its shared combined limit (pooled — stacks or items, per the group's
    // count mode), an optional name, and one UNIFIED search box that both filters the current member list
    // (X to remove) and offers matching non-members to add (+). The limit, count mode, and name apply
    // live. Closes itself when the group no longer exists (e.g. its last member was removed).
    public class PscGroupEditorWindow : Window
    {
        private readonly StorageSettings settings;
        private readonly PscHaulUnit unit;
        private readonly PscLimitGroup group;
        private readonly PscLimitEditor editor = new PscLimitEditor { pooled = true };

        private string nameBuf;
        private string searchBuf = "";
        private readonly QuickSearchFilter searchFilter = new QuickSearchFilter();
        private Vector2 memberScroll;
        private Vector2 addScroll;

        public override Vector2 InitialSize => new Vector2(720f, 600f);

        public PscGroupEditorWindow(StorageSettings settings, PscHaulUnit unit, PscLimitGroup group)
        {
            this.settings = settings;
            this.unit = unit;
            this.group = group;
            nameBuf = group?.name ?? "";
            // The pooled editor edits the limit in the group's own unit; seed its mode from the group, then
            // load the raw values (LoadFrom preserves stacksMode for pooled).
            editor.stacksMode = group != null && group.countMode == PscGroupCountMode.Stacks;
            editor.LoadFrom(group?.limit);
            doCloseX = true;
            draggable = true;
            // No closeOnClickedOutside: this window is opened from inside a right-click FloatMenuOption
            // callback, and the same mouse event would be read as a click-outside and self-close it.
            // Dismissal is covered by the X and the explicit Close button.
            layer = WindowLayer.Super;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var data = PscStorageDataStore.TryGet(settings);
            if (data == null || group == null || !data.limitGroups.Contains(group)) { Close(false); return; }

            // Member-derived stack context: uniform vs mixed stack sizes drives the editor's stacks/items
            // conversion. Rebuilt each frame because membership changes live.
            var target = PscLimitEditorTarget.FromDefs(group.members);

            var list = new Listing_Standard();
            list.Begin(inRect);

            Text.Font = GameFont.Medium;
            list.Label(string.IsNullOrEmpty(group.name)
                ? "PSC_GroupTitleLetter".Translate(LetterOf())
                : "PSC_GroupTitleNamed".Translate(LetterOf(), group.name));
            Text.Font = GameFont.Small;
            list.GapLine();

            // Optional name (applies live). Auto-size the label so "(optional)" never clips.
            var nameRow = list.GetRect(28f);
            float nameLabelW = Text.CalcSize("PSC_GroupName".Translate()).x + 10f;
            Widgets.Label(new Rect(nameRow.x, nameRow.y + 4f, nameLabelW, nameRow.height), "PSC_GroupName".Translate());
            string editedName = Widgets.TextField(
                new Rect(nameRow.x + nameLabelW, nameRow.y, nameRow.width - nameLabelW, nameRow.height), nameBuf ?? "");
            if (editedName != nameBuf)
            {
                nameBuf = editedName;
                group.name = string.IsNullOrEmpty(nameBuf) ? null : nameBuf;
            }

            list.Gap(6f);

            // Shared combined limit (pooled). Apply live when the values OR the count mode change (a
            // mixed-stack toggle keeps the numbers, so the mode change must be tracked too).
            int? prevLower = editor.lowerVal, prevUpper = editor.upperVal;
            bool prevStacks = editor.stacksMode;
            editor.Draw(list, unit, target);
            if (editor.lowerVal != prevLower || editor.upperVal != prevUpper || editor.stacksMode != prevStacks)
                PscEdit.ApplyGroupLimit(settings, unit, group, editor.ToRawLimit(),
                    editor.stacksMode ? PscGroupCountMode.Stacks : PscGroupCountMode.Items);

            list.Gap(6f);
            list.Label("PSC_Preview".Translate(editor.PreviewString(target)));
            DrawNowLine(list, data);
            list.GapLine();

            if (group.members.Count == 1)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = PscUiTheme.NoteText;
                list.Label("PSC_GroupOneMemberHint".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }

            // Unified search on top: filters the member list AND surfaces matching non-members to add.
            var searchRow = list.GetRect(28f);
            searchBuf = Widgets.TextField(searchRow, searchBuf ?? "");
            searchFilter.Text = searchBuf;
            bool searching = searchFilter.Active;

            list.Gap(4f);

            // One content band, buttons pinned at the bottom. GetRect(0) reports the cursor without
            // consuming space; Listing.Begin did BeginGroup(inRect), so curY is 0-based and the usable
            // bottom is inRect.height (NOT inRect.yMax). When searching, members (left) and add (right) sit
            // SIDE BY SIDE so adding an item never shrinks the member list.
            float curY = list.GetRect(0f).y;
            float reserved = 8f + 30f;                       // gap + button row
            float bandH = Mathf.Max(80f, inRect.height - curY - reserved);
            var band = list.GetRect(bandH);
            float headerH = Text.LineHeight;

            if (searching)
            {
                float gap = 8f;
                float colW = (band.width - gap) / 2f;
                DrawColumn(new Rect(band.x, band.y, colW, band.height), headerH,
                    "PSC_GroupMembersHeader".Translate(group.members.Count), r => DrawMemberList(r, true));
                DrawColumn(new Rect(band.x + colW + gap, band.y, colW, band.height), headerH,
                    "PSC_GroupAddMatching".Translate(), r => DrawAddCandidates(r, data));
            }
            else
            {
                DrawColumn(band, headerH, "PSC_GroupMembersHeader".Translate(group.members.Count),
                    r => DrawMemberList(r, false));
            }

            list.Gap(8f);
            var btnRow = list.GetRect(30f);
            if (Widgets.ButtonText(new Rect(btnRow.x, btnRow.y, btnRow.width / 2f - 4f, btnRow.height),
                    "PSC_GroupUngroup".Translate()))
            {
                PscEdit.DissolveGroup(settings, group);
                Close();
            }
            if (Widgets.ButtonText(new Rect(btnRow.x + btnRow.width / 2f + 4f, btnRow.y, btnRow.width / 2f - 4f, btnRow.height),
                    "PSC_Close".Translate()))
            {
                Close();
            }

            list.End();
        }

        private string LetterOf() => string.IsNullOrEmpty(group.letter) ? "?" : group.letter;

        // A header label at the top of `outer`, then `body` on the rect below it. Used for the
        // side-by-side Members / Add columns (and the single full-width member column when not searching).
        private static void DrawColumn(Rect outer, float headerH, string header, System.Action<Rect> body)
        {
            Widgets.Label(new Rect(outer.x, outer.y, outer.width, headerH), header);
            body(new Rect(outer.x, outer.y + headerH, outer.width, outer.height - headerH));
        }

        // Live "now: N / cap unit" read-out: the enforced (mode-aware) current count vs the upper cap, so
        // a full group's "no more intake" state is visible. Uses the same enforced count the admission
        // gate reads (packed stacks in Stacks mode, item sum in Items mode).
        private void DrawNowLine(Listing_Standard list, PscStorageData data)
        {
            int now = data.GroupEnforcedCount(group, unit);
            string unitWord = (group.countMode == PscGroupCountMode.Stacks
                ? "PSC_ModeStacks" : "PSC_ModeItems").Translate();
            string cap = group.limit != null && group.limit.Upper.HasValue
                ? group.limit.Upper.Value.ToString() : "PSC_Maximum".Translate().ToString();
            Text.Font = GameFont.Tiny;
            GUI.color = PscUiTheme.NoteText;
            list.Label("PSC_GroupNow".Translate(now, cap, unitWord));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private void DrawMemberList(Rect outer, bool searching)
        {
            Widgets.DrawMenuSection(outer);
            var inner = outer.ContractedBy(4f);

            // Filter the member list by the unified search (all members when not searching).
            var members = group.members;
            var shown = new List<ThingDef>();
            for (int i = 0; i < members.Count; i++)
            {
                var d = members[i];
                if (d == null) continue;
                if (!searching || PscSearchMatch.Matches(searchFilter, d)) shown.Add(d);
            }

            float rowH = 26f;
            var view = new Rect(0f, 0f, inner.width - 16f, shown.Count * rowH);
            Widgets.BeginScrollView(inner, ref memberScroll, view);
            float y = 0f;
            ThingDef toRemove = null;
            for (int i = 0; i < shown.Count; i++)
            {
                var d = shown[i];
                var row = new Rect(0f, y, view.width, rowH);
                if (i % 2 == 1) Widgets.DrawLightHighlight(row);
                Widgets.ThingIcon(new Rect(row.x, row.y + 2f, 22f, 22f), d);
                Widgets.Label(new Rect(row.x + 26f, row.y, row.width - 26f - 26f, rowH), d.LabelCap);
                if (Widgets.ButtonText(new Rect(row.xMax - 24f, row.y + 2f, 22f, 22f), "X"))
                    toRemove = d;
                y += rowH;
            }
            Widgets.EndScrollView();
            if (toRemove != null) PscEdit.RemoveFromGroup(settings, toRemove);
        }

        private void DrawAddCandidates(Rect outer, PscStorageData data)
        {
            Widgets.DrawMenuSection(outer);
            var inner = outer.ContractedBy(4f);
            var cands = AddCandidates(data);
            float rowH = 26f;
            var view = new Rect(0f, 0f, inner.width - 16f, cands.Count * rowH);
            Widgets.BeginScrollView(inner, ref addScroll, view);
            float y = 0f;
            ThingDef toAdd = null;
            for (int i = 0; i < cands.Count; i++)
            {
                var d = cands[i];
                var row = new Rect(0f, y, view.width, rowH);
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                Widgets.ThingIcon(new Rect(row.x, row.y + 2f, 22f, 22f), d);
                Widgets.Label(new Rect(row.x + 26f, row.y, row.width - 26f - 24f, rowH), d.LabelCap);
                if (Widgets.ButtonText(new Rect(row.xMax - 24f, row.y + 2f, 22f, 22f), "+"))
                    toAdd = d;
                y += rowH;
            }
            Widgets.EndScrollView();
            if (toAdd != null) PscEdit.AddToGroup(settings, unit, group, toAdd);
        }

        // Storable, player-acquirable defs this unit can hold that match the search and are NOT already in
        // a group. Capped so the list never explodes; mirrors PscControlWindow's filtering.
        private List<ThingDef> AddCandidates(PscStorageData data)
        {
            var result = new List<ThingDef>();
            if (!searchFilter.Active) return result;
            ThingFilter parentFilter = settings.owner?.GetParentStoreSettings()?.filter;
            var all = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count && result.Count < 30; i++)
            {
                var d = all[i];
                if (!d.EverStorable(false) || !d.PlayerAcquirable || d.virtualDefParent != null) continue;
                if (data.GroupOf(d) != null) continue;
                if (!PscSearchMatch.Matches(searchFilter, d)) continue;
                if (parentFilter != null)
                {
                    if (!parentFilter.Allows(d)) continue;
                    if (parentFilter.IsAlwaysDisallowedDueToSpecialFilters(d)) continue;
                }
                result.Add(d);
            }
            return result;
        }
    }
}
