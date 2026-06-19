using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // The PSC control window (design §10.2-10.5). Set a maximum / refill-threshold pair (via the
    // shared PscLimitEditor) and apply or remove it across the items matched by the vanilla storage
    // tab's search box, plus a per-unit batch (minimum items per trip). Limits are tracked in items.
    public class PscControlWindow : Window
    {
        private readonly StorageSettings settings;
        private readonly PscHaulUnit unit;
        private readonly QuickSearchFilter search;
        private readonly PscLimitEditor editor = new PscLimitEditor();

        private int batchVal;
        private string batchBuf = "0";
        private int batchEmptyVal;
        private string batchEmptyBuf = "0";

        // Per-unit default limit ("all items"), in items. Applied immediately on edit, like batch.
        private const int DefaultItemMax = 1000000;
        private int? defaultLowerVal;
        private int? defaultUpperVal;
        private string defaultLowerBuf = "";
        private string defaultUpperBuf = "";

        public override Vector2 InitialSize => new Vector2(520f, 470f);

        public PscControlWindow(StorageSettings settings, PscHaulUnit unit, QuickSearchFilter search)
        {
            this.settings = settings;
            this.unit = unit;
            this.search = search;
            var existing = PscStorageDataStore.TryGet(settings);
            if (existing != null)
            {
                batchVal = existing.batch;
                batchBuf = batchVal.ToString();
                batchEmptyVal = existing.batchEmpty;
                batchEmptyBuf = batchEmptyVal.ToString();
                if (existing.defaultLimit != null && !existing.defaultLimit.IsDefault)
                {
                    defaultLowerVal = existing.defaultLimit.Lower;
                    defaultUpperVal = existing.defaultLimit.Upper;
                    defaultLowerBuf = defaultLowerVal?.ToString() ?? "";
                    defaultUpperBuf = defaultUpperVal?.ToString() ?? "";
                }
            }
            doCloseX = true;
            draggable = true;
            closeOnClickedOutside = false;
            preventCameraMotion = false;
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

            var target = PscLimitEditorTarget.FromDefs(MatchedDefs());
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

            list.Gap(8f);
            list.Label("PSC_Preview".Translate(editor.PreviewString(target)));

            Text.Font = GameFont.Tiny;
            GUI.color = PscUiTheme.NoteText;
            list.Label("PSC_SoftCapNote".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            list.Gap(10f);
            bool canApply = search != null && search.Active;
            var rowRect = list.GetRect(34f);
            var leftBtn = new Rect(rowRect.x, rowRect.y, rowRect.width / 2f - 4f, rowRect.height);
            var rightBtn = new Rect(rowRect.x + rowRect.width / 2f + 4f, rowRect.y, rowRect.width / 2f - 4f, rowRect.height);

            if (DrawButton(leftBtn, "PSC_ApplyToSearch".Translate(), canApply)) ApplyToSearch();
            if (DrawButton(rightBtn, "PSC_RemoveFromSearch".Translate(), canApply)) RemoveFromSearch();

            if (!canApply)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = PscUiTheme.DisabledHintText;
                list.Gap(4f);
                list.Label("PSC_SearchHint".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }

            DrawDefaultSection(list);

            list.End();
        }

        // Per-unit "all items" default limit: a refill/maximum pair (in items) applied to any
        // allowed def with no explicit per-def limit. Applied immediately on edit, like batch.
        // This is also where limits imported from other stockpile mods land (see PscMigration).
        private void DrawDefaultSection(Listing_Standard list)
        {
            list.Gap(8f);
            list.GapLine();
            list.Label("PSC_DefaultLimitHeader".Translate());
            Text.Font = GameFont.Tiny;
            GUI.color = PscUiTheme.NoteText;
            list.Label("PSC_DefaultLimitNote".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            var row = list.GetRect(28f);
            float half = row.width / 2f - 4f;
            const float labelW = 56f;
            var lowerLabel = new Rect(row.x, row.y, labelW, row.height);
            var lowerField = new Rect(row.x + labelW, row.y + 2f, half - labelW, 24f);
            var upperLabel = new Rect(row.x + row.width / 2f + 4f, row.y, labelW, row.height);
            var upperField = new Rect(upperLabel.xMax, row.y + 2f, half - labelW, 24f);

            var pa = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(lowerLabel, "PSC_LowerShort".Translate());
            Widgets.Label(upperLabel, "PSC_UpperShort".Translate());
            Text.Anchor = pa;

            int? prevLower = defaultLowerVal;
            int? prevUpper = defaultUpperVal;
            PscLimitEditor.DrawNullableField(lowerField, ref defaultLowerVal, ref defaultLowerBuf, 0, DefaultItemMax);
            PscLimitEditor.DrawNullableField(upperField, ref defaultUpperVal, ref defaultUpperBuf, 1, DefaultItemMax);
            TooltipHandler.TipRegion(row, "PSC_DefaultLimitTip".Translate());

            if (defaultLowerVal != prevLower || defaultUpperVal != prevUpper) ApplyDefault();
        }

        // Default limit is a single per-unit value (not per-def), so it applies immediately on edit.
        private void ApplyDefault()
        {
            int? upper = defaultUpperVal;
            int? lower = defaultLowerVal;
            if (upper.HasValue && lower.HasValue && lower.Value > upper.Value) lower = upper;

            var data = PscStorageDataStore.GetOrCreate(settings);
            if (data.defaultLimit == null) data.defaultLimit = new PscDefLimit();
            data.defaultLimit.Upper = upper;
            data.defaultLimit.Lower = lower;
            data.Notify_DefaultLimitSet(unit);
            PscMapComponent.NotifyPolicyChanged(settings);
        }

        private static bool DrawButton(Rect rect, string label, bool enabled)
        {
            bool prev = GUI.enabled;
            GUI.enabled = enabled;
            bool clicked = Widgets.ButtonText(rect, label) && enabled;
            GUI.enabled = prev;
            return clicked;
        }

        private IEnumerable<ThingDef> MatchedDefs()
        {
            ThingFilter parentFilter = settings.owner?.GetParentStoreSettings()?.filter;
            var all = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                var d = all[i];
                if (!d.EverStorable(false)) continue;
                if (!PscSearchMatch.Matches(search, d)) continue;
                if (parentFilter != null && !parentFilter.Allows(d)) continue;
                yield return d;
            }
        }

        private void ApplyToSearch()
        {
            foreach (var d in MatchedDefs()) PscEdit.ApplyLimit(settings, unit, d, editor.ToLimit(d));
            PscMapComponent.NotifyPolicyChanged(settings);
        }

        private void RemoveFromSearch()
        {
            foreach (var d in MatchedDefs()) PscEdit.ClearLimit(settings, d, false);
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
                    return ReferenceEquals(group?.GetStoreSettings(), settings);
                }

                var parent = StoreParentFromSelected(selected[0]);
                return ReferenceEquals(parent?.GetStoreSettings(), settings);
            }
            catch
            {
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
