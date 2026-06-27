using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Editor for one limit group: its shared combined limit (pooled, raw item totals), an optional
    // name, the member list (X to remove), a search-to-add box (defs not already grouped), and Ungroup.
    // The limit and name apply live; membership edits go through PscEdit (which re-normalizes + notifies).
    // Closes itself when the group no longer exists (e.g. it auto-dissolved below the 2-member minimum).
    public class PscGroupEditorWindow : Window
    {
        private readonly StorageSettings settings;
        private readonly PscHaulUnit unit;
        private readonly PscLimitGroup group;
        private readonly PscLimitEditor editor = new PscLimitEditor { pooled = true };

        private string nameBuf;
        private string addBuf = "";
        private readonly QuickSearchFilter addFilter = new QuickSearchFilter();
        private Vector2 memberScroll;
        private Vector2 addScroll;

        public override Vector2 InitialSize => new Vector2(540f, 560f);

        public PscGroupEditorWindow(StorageSettings settings, PscHaulUnit unit, PscLimitGroup group)
        {
            this.settings = settings;
            this.unit = unit;
            this.group = group;
            nameBuf = group?.name ?? "";
            editor.LoadFrom(group?.limit);
            doCloseX = true;
            draggable = true;
            closeOnClickedOutside = true;
            layer = WindowLayer.Super;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var data = PscStorageDataStore.TryGet(settings);
            if (data == null || group == null || !data.limitGroups.Contains(group)) { Close(false); return; }

            var list = new Listing_Standard();
            list.Begin(inRect);

            Text.Font = GameFont.Medium;
            list.Label("PSC_GroupEditorTitle".Translate(string.IsNullOrEmpty(group.letter) ? "?" : group.letter));
            Text.Font = GameFont.Small;
            list.GapLine();

            // Optional name (applies live).
            var nameRow = list.GetRect(Text.LineHeight + 4f);
            var nameLabel = new Rect(nameRow.x, nameRow.y, 90f, nameRow.height);
            var nameField = new Rect(nameLabel.xMax + 6f, nameRow.y, nameRow.width - nameLabel.width - 6f, nameRow.height);
            Widgets.Label(nameLabel, "PSC_GroupName".Translate());
            string editedName = Widgets.TextField(nameField, nameBuf ?? "");
            if (editedName != nameBuf)
            {
                nameBuf = editedName;
                group.name = string.IsNullOrEmpty(nameBuf) ? null : nameBuf;
            }

            list.Gap(6f);

            // Shared combined limit (pooled, raw items). Apply live when it changes.
            int? prevLower = editor.lowerVal, prevUpper = editor.upperVal;
            editor.Draw(list, unit);
            if (editor.lowerVal != prevLower || editor.upperVal != prevUpper)
                PscEdit.ApplyGroupLimit(settings, unit, group, editor.ToRawLimit());

            list.Gap(6f);
            list.Label("PSC_Preview".Translate(editor.PreviewString()));
            list.GapLine();

            // Member list with remove buttons.
            list.Label("PSC_GroupMembersHeader".Translate(group.members.Count));
            float memberAreaH = 130f;
            var memberOuter = list.GetRect(memberAreaH);
            DrawMemberList(memberOuter);

            list.Gap(6f);

            // Add-by-search box.
            list.Label("PSC_GroupAddHeader".Translate());
            var searchRow = list.GetRect(28f);
            addBuf = Widgets.TextField(searchRow, addBuf ?? "");
            addFilter.Text = addBuf;
            var addOuter = list.GetRect(110f);
            DrawAddCandidates(addOuter, data);

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

        private void DrawMemberList(Rect outer)
        {
            Widgets.DrawMenuSection(outer);
            var inner = outer.ContractedBy(4f);
            var members = group.members;
            float rowH = 26f;
            var view = new Rect(0f, 0f, inner.width - 16f, members.Count * rowH);
            Widgets.BeginScrollView(inner, ref memberScroll, view);
            float y = 0f;
            ThingDef toRemove = null;
            for (int i = 0; i < members.Count; i++)
            {
                var d = members[i];
                if (d == null) continue;
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
                if (Widgets.ButtonInvisible(row)) toAdd = d;
                Widgets.Label(new Rect(row.x + 26f, row.y, row.width - 26f, rowH), d.LabelCap);
                y += rowH;
            }
            Widgets.EndScrollView();
            if (toAdd != null) { PscEdit.AddToGroup(settings, unit, group, toAdd); addBuf = ""; }
        }

        // Storable, player-acquirable defs this unit can hold that match the add-search and are NOT
        // already in a group. Capped so the list never explodes; mirrors PscControlWindow's filtering.
        private List<ThingDef> AddCandidates(PscStorageData data)
        {
            var result = new List<ThingDef>();
            if (!addFilter.Active) return result;
            ThingFilter parentFilter = settings.owner?.GetParentStoreSettings()?.filter;
            var all = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count && result.Count < 30; i++)
            {
                var d = all[i];
                if (!d.EverStorable(false) || !d.PlayerAcquirable || d.virtualDefParent != null) continue;
                if (data.GroupOf(d) != null) continue;
                if (!PscSearchMatch.Matches(addFilter, d)) continue;
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
