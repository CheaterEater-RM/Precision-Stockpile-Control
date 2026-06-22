using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // The PSC control window (design §10.2-10.5). Set a maximum / refill-threshold pair (via the
    // shared PscLimitEditor) and apply or remove it across the items the storage tab's search box
    // matches — or, with no search, every item this storage allows — plus a per-unit batch (minimum
    // items per trip). Limits are tracked in items.
    public class PscControlWindow : Window
    {
        private StorageSettings settings;
        private PscHaulUnit unit;
        private QuickSearchFilter search;
        private readonly PscLimitEditor editor = new PscLimitEditor();

        // The storage this window is currently editing. The FillTab postfix reads this to decide
        // whether the selection changed and the window needs retargeting.
        public StorageSettings Settings => settings;

        private int batchVal;
        private string batchBuf = "0";
        private int batchEmptyVal;
        private string batchEmptyBuf = "0";
        private int perTileVal;
        private string perTileBuf = "0";

        public override Vector2 InitialSize => new Vector2(520f, 380f);

        public PscControlWindow(StorageSettings settings, PscHaulUnit unit, QuickSearchFilter search)
        {
            Retarget(settings, unit, search);
            doCloseX = true;
            draggable = true;
            closeOnClickedOutside = false;
            preventCameraMotion = false;
        }

        // Point this window at a (possibly different) selected storage, reloading its per-unit batch
        // fields. The FillTab postfix calls this when the player selects a new
        // stockpile, so the open window follows the selection instead of closing. The staged limit
        // editor is intentionally left as-is — it is a scratch value, not bound to one stockpile.
        public void Retarget(StorageSettings settings, PscHaulUnit unit, QuickSearchFilter search)
        {
            this.settings = settings;
            this.unit = unit;
            this.search = search;

            batchVal = 0;
            batchBuf = "0";
            batchEmptyVal = 0;
            batchEmptyBuf = "0";
            perTileVal = 0;
            perTileBuf = "0";

            var existing = PscStorageDataStore.TryGet(settings);
            if (existing != null)
            {
                batchVal = existing.batch;
                batchBuf = batchVal.ToString();
                batchEmptyVal = existing.batchEmpty;
                batchEmptyBuf = batchEmptyVal.ToString();
                perTileVal = existing.perTileLimit;
                perTileBuf = perTileVal.ToString();
            }
        }

        public override void WindowUpdate()
        {
            base.WindowUpdate();
            if (!StillSelected()) Close(false);
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (!StillSelected())
            {
                Close(false);
                return;
            }

            var list = new Listing_Standard();
            list.Begin(inRect);

            Text.Font = GameFont.Medium;
            list.Label("PSC_WindowTitle".Translate());
            Text.Font = GameFont.Small;
            list.GapLine();

            var target = PscLimitEditorTarget.FromDefs(CurrentDefs());
            editor.Draw(list, unit, target);

            list.Gap(6f);
            var batchRow = list.GetRect(Text.LineHeight);
            float halfWidth = batchRow.width / 2f - 4f;
            var fillRect = new Rect(batchRow.x, batchRow.y, halfWidth, batchRow.height);
            var emptyRect = new Rect(batchRow.x + batchRow.width / 2f + 4f, batchRow.y, halfWidth, batchRow.height);

            int prevBatch = batchVal;
            Widgets.TextFieldNumericLabeled(fillRect, "PSC_BatchFill".Translate(), ref batchVal, ref batchBuf, 0, 5000);
            TooltipHandler.TipRegion(fillRect, "PSC_BatchFillTip".Translate());
            if (batchVal != prevBatch) ApplyBatch();

            int prevBatchEmpty = batchEmptyVal;
            Widgets.TextFieldNumericLabeled(emptyRect, "PSC_BatchEmpty".Translate(), ref batchEmptyVal, ref batchEmptyBuf, 0, 5000);
            TooltipHandler.TipRegion(emptyRect, "PSC_BatchEmptyTip".Translate());
            if (batchEmptyVal != prevBatchEmpty) ApplyBatchEmpty();

            // Per-cell ("Max per cell") field: a single per-unit value, applied immediately on edit like
            // batch. Shown only when the master setting is on AND this selection is a floor stockpile
            // (the per-tile cap is floor-only; shelves and linked storage groups never show it).
            if (PscMod.Settings.perTileLimits && IsFloorStockpile())
            {
                list.Gap(6f);
                var perTileRow = list.GetRect(Text.LineHeight);
                var perTileRect = new Rect(perTileRow.x, perTileRow.y, halfWidth, perTileRow.height);
                int prevPerTile = perTileVal;
                Widgets.TextFieldNumericLabeled(perTileRect, "PSC_PerTileLimit".Translate(), ref perTileVal, ref perTileBuf, 0, 5000);
                TooltipHandler.TipRegion(perTileRect, "PSC_PerTileLimitTip".Translate());
                if (perTileVal != prevPerTile) ApplyPerTile();
            }

            list.Gap(8f);
            list.Label("PSC_Preview".Translate(editor.PreviewString(target)));

            Text.Font = GameFont.Tiny;
            GUI.color = PscUiTheme.NoteText;
            list.Label("PSC_SoftCapNote".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            list.Gap(10f);
            bool searching = search != null && search.Active;
            var rowRect = list.GetRect(34f);
            var leftBtn = new Rect(rowRect.x, rowRect.y, rowRect.width / 2f - 4f, rowRect.height);
            var rightBtn = new Rect(rowRect.x + rowRect.width / 2f + 4f, rowRect.y, rowRect.width / 2f - 4f, rowRect.height);

            if (Widgets.ButtonText(leftBtn, (searching ? "PSC_ApplyToSearch" : "PSC_ApplyToAll").Translate())) ApplyToCurrent();
            if (Widgets.ButtonText(rightBtn, (searching ? "PSC_RemoveFromSearch" : "PSC_RemoveFromAll").Translate())) RemoveFromCurrent();

            Text.Font = GameFont.Tiny;
            GUI.color = PscUiTheme.NoteText;
            list.Gap(4f);
            list.Label((searching ? "PSC_ApplyToSearchHint" : "PSC_ApplyToAllHint").Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            list.End();
        }

        // The set the editor's limit is shown for and applied to. With a search active, that's what
        // the unit's filter tree shows for the search (WYSIWYG, see MatchedDefs); with no search,
        // it's every item this storage currently allows ("apply to all allowed items").
        private IEnumerable<ThingDef> CurrentDefs()
        {
            if (search != null && search.Active) return MatchedDefs();
            return AllowedDefs();
        }

        // Items currently allowed (checked) in this unit. You can't allow what the unit can't store,
        // so this is inherently WYSIWYG and bounded — no need to scan the whole def database.
        private IEnumerable<ThingDef> AllowedDefs()
        {
            foreach (var d in settings.filter.AllowedThingDefs)
                if (d != null && d.EverStorable(false)) yield return d;
        }

        private IEnumerable<ThingDef> MatchedDefs()
        {
            // Mirror the vanilla storage tree's Visible(ThingDef) so the item/stack-mode decision and
            // the "Apply to search" set match exactly what the player sees for THIS unit (WYSIWYG).
            // A shelf's parent filter (its fixedStorageSettings) already rejects whole categories like
            // Plants/Buildings via Allows(); the special-filter guard catches the rest (items a shelf
            // hides but Allows(ThingDef) wouldn't), so a shelf's "wood" search stays just wood logs.
            ThingFilter parentFilter = settings.owner?.GetParentStoreSettings()?.filter;
            var all = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                var d = all[i];
                if (!d.EverStorable(false)) continue;
                if (!d.PlayerAcquirable) continue;
                if (d.virtualDefParent != null) continue;
                if (!PscSearchMatch.Matches(search, d)) continue;
                if (parentFilter != null)
                {
                    if (!parentFilter.Allows(d)) continue;
                    if (parentFilter.IsAlwaysDisallowedDueToSpecialFilters(d)) continue;
                }
                yield return d;
            }
        }

        private void ApplyToCurrent()
        {
            var defs = new List<ThingDef>(CurrentDefs());
            for (int i = 0; i < defs.Count; i++)
                PscEdit.ApplyLimit(settings, unit, defs[i], editor.ToLimit(defs[i]));
            PscMapComponent.NotifyPolicyChanged(settings);
        }

        // Clear the limit across the current set. With a search active we also un-allow the matched
        // items (mirrors "Apply to search" enabling them); with no search we keep everything allowed
        // and only drop the limits, so "Remove from all" never empties the storage's own filter.
        private void RemoveFromCurrent()
        {
            bool searching = search != null && search.Active;
            var defs = new List<ThingDef>(CurrentDefs());
            for (int i = 0; i < defs.Count; i++)
                PscEdit.ClearLimit(settings, defs[i], !searching);
            PscMapComponent.NotifyPolicyChanged(settings);
        }

        // Batch is a single per-unit value (not per-def), so it applies immediately on edit.
        private void ApplyBatch()
        {
            PscStorageDataStore.GetOrCreate(settings).batch = batchVal;
            PscMapComponent.NotifyPolicyChanged(settings);
        }

        // Batch empty mirrors batch fill: a single per-unit value, applied immediately on edit.
        private void ApplyBatchEmpty()
        {
            PscStorageDataStore.GetOrCreate(settings).batchEmpty = batchEmptyVal;
            PscMapComponent.NotifyPolicyChanged(settings);
        }

        // Per-cell cap: a single per-unit value, applied immediately on edit. NotifyPolicyChanged now wakes
        // the unit's cells in the haulables lister (via owner.Notify_SettingsChanged), so existing over-cap
        // piles start spreading at once (the relocation prefix flags them haulable) without a bespoke
        // RecalcAllInCells here.
        private void ApplyPerTile()
        {
            PscStorageDataStore.GetOrCreate(settings).perTileLimit = perTileVal;
            PscMapComponent.NotifyPolicyChanged(settings);
        }

        // True when the current selection is a FLOOR stockpile (Zone_Stockpile slot group). A linked
        // StorageGroup unit, a shelf, or deep storage resolves to a non-SlotGroup or a non-stockpile
        // parent, so the per-cell control stays hidden for them (per-tile is floor-only).
        private bool IsFloorStockpile()
        {
            return unit.group is SlotGroup slot && slot.parent is Zone_Stockpile;
        }

        // True while *some* storage is selected (a single unit or one unified storage group). The
        // FillTab postfix retargets this window to whatever storage is selected, so we close only
        // when the selection is no longer a storage at all — not merely when it changed.
        private bool StillSelected()
        {
            try
            {
                var selected = Find.Selector.SelectedObjectsListForReading;
                if (selected == null || selected.Count == 0) return false;
                if (selected.Count > 1)
                {
                    StorageGroup group = null;
                    for (int i = 0; i < selected.Count; i++)
                    {
                        if (!(selected[i] is IStorageGroupMember member) || member.Group == null) return false;
                        if (group == null) group = member.Group;
                        else if (!ReferenceEquals(group, member.Group)) return false;
                    }
                    return group?.GetStoreSettings() != null;
                }

                var parent = StoreParentFromSelected(selected[0]);
                if (parent == null || parent.GetStoreSettings() == null) return false;
                // Excluded storage (grave / bookcase / gene bank / comp-backed special storage): the FillTab
                // prefix hides PSC controls for these and won't retarget the window to them, so close rather
                // than linger over a unit PSC can't edit (F5).
                if (PscStorageButtonFilter.ShouldHide(parent)) return false;
                return true;
            }
            catch (Exception ex)
            {
                // Closing on an unexpected throw is the safe fallback, but log once so a genuine UI-state /
                // version-drift break leaves a trail instead of a silently vanishing window (F7).
                Log.WarningOnce("[PSC] PscControlWindow.StillSelected threw; closing the window. " + ex, 0x1C5A0011);
                return false;
            }
        }

        private static IStoreSettingsParent StoreParentFromSelected(object selected)
        {
            if (selected is IStoreSettingsParent parent) return parent;
            if (selected is ThingWithComps twc && twc.AllComps != null)
            {
                for (int i = 0; i < twc.AllComps.Count; i++)
                {
                    if (twc.AllComps[i] is IStoreSettingsParent compParent) return compParent;
                }
            }
            return null;
        }
    }
}
