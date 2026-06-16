using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Per-item / per-category limit submenu (design §10.4). Reproduces the global limit controls
    // scoped to one def (right-click a row) or a category's descendant defs (right-click a category),
    // with Apply / Cancel and quick green-check (allow + clear limit) / red-x (disallow + clear limit).
    public class PscItemLimitMenu : Window
    {
        private readonly StorageSettings settings;
        private readonly PscHaulUnit unit;
        private readonly List<ThingDef> defs;
        private readonly string title;
        private readonly PscLimitEditor editor = new PscLimitEditor();
        private readonly PscLimitEditorTarget target;

        public override Vector2 InitialSize => new Vector2(520f, 310f);

        public PscItemLimitMenu(StorageSettings settings, PscHaulUnit unit, List<ThingDef> defs, string title)
        {
            this.settings = settings;
            this.unit = unit;
            this.defs = defs;
            this.title = title;
            target = PscLimitEditorTarget.FromDefs(defs);

            var data = PscStorageDataStore.TryGet(settings);
            editor.LoadFrom(data != null && defs.Count > 0 ? data.GetLimit(defs[0]) : null, target);

            doCloseX = true;
            draggable = true;
            closeOnClickedOutside = true;
            layer = WindowLayer.Super;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var list = new Listing_Standard();
            list.Begin(inRect);

            Text.Font = GameFont.Small;
            list.Label(title);
            list.GapLine();

            editor.Draw(list, unit, target);
            list.Gap(6f);
            list.Label("PSC_Preview".Translate(editor.PreviewString(target)));

            list.Gap(10f);
            var r1 = list.GetRect(30f);
            float w = r1.width / 2f - 4f;
            if (Widgets.ButtonText(new Rect(r1.x, r1.y, w, r1.height), "PSC_Apply".Translate())) { Apply(); Close(); }
            if (Widgets.ButtonText(new Rect(r1.x + w + 8f, r1.y, w, r1.height), "PSC_Cancel".Translate())) Close();

            list.Gap(6f);
            var r2 = list.GetRect(28f);
            if (Widgets.ButtonText(new Rect(r2.x, r2.y, w, r2.height), "PSC_AllowClear".Translate())) { Clear(true); Close(); }
            if (Widgets.ButtonText(new Rect(r2.x + w + 8f, r2.y, w, r2.height), "PSC_DisallowClear".Translate())) { Clear(false); Close(); }

            list.End();
        }

        private void Apply()
        {
            foreach (var d in defs) PscEdit.ApplyLimit(settings, unit, d, editor.ToLimit(d));
            PscMapComponent.NotifyPolicyChanged(settings);
        }

        private void Clear(bool allow)
        {
            foreach (var d in defs) PscEdit.ClearLimit(settings, d, allow);
            PscMapComponent.NotifyPolicyChanged(settings);
        }
    }
}
