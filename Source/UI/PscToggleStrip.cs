using System;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace PrecisionStockpileControl
{
    // The quick-toggle strip on the storage tab, drawn to the right of the (narrowed) Stockpile
    // Control button on row 2 (see PscUiWidgets.EntryButtonRect / ITab_Storage_FillTab_Patch).
    //
    // Five square on/off icons mirroring the most-used per-stockpile policy: batch-fill, batch-empty,
    // pull-only-from-sources, push-only-to-destinations, alarm. Each one reads and writes the SAME
    // PscStorageData the inspector gizmos use (PscFeederGizmos / PscAlarmGizmo / the control window),
    // so the strip and those gizmos stay in sync automatically. Drawing never creates policy — an
    // untouched stockpile shows every icon off and only the first click calls GetOrCreate.
    //
    // Visual convention (Council call): full-colour + a "selected" highlight when on, desaturated/dim
    // when off, dimmer still when unavailable. No X-overlay — a red X reads as "forbidden" here.
    [StaticConstructorOnStartup]
    public static class PscToggleStrip
    {
        private const float IconSize = 24f;     // square, matches the button height
        private const float IconGap = 3f;       // between icons
        private const float StripGap = 8f;      // between the button and the first icon

        // On toggle-on, batch with no prior value starts here; the exact number is set in the control
        // window. Mirrors PscAlarmGizmo's right-click default for the alarm's high side.
        private const int DefaultBatchSize = 10;
        private const int DefaultHighPct = 90;

        private static readonly Texture2D BatchInTex = Load("UI/Toggles/BatchIn");
        private static readonly Texture2D BatchOutTex = Load("UI/Toggles/BatchOut");
        private static readonly Texture2D OnlyFromTex = Load("UI/Toggles/OnlyFromSource");
        private static readonly Texture2D OnlyToTex = Load("UI/Toggles/OnlyToDestinations");
        private static readonly Texture2D AlarmTex = Load("UI/Toggles/Alarm");

        private static readonly Color OffIcon = new Color(0.62f, 0.62f, 0.62f, 0.55f);   // desaturated/dim
        private static readonly Color DisabledIcon = new Color(0.5f, 0.5f, 0.5f, 0.30f); // unavailable

        private static Texture2D Load(string path) => ContentFinder<Texture2D>.Get(path, reportFailure: false) ?? BaseContent.BadTex;

        // Drawn in the FillTab postfix's window space (origin x=10), anchored to the entry button.
        public static void Draw(StorageSettings settings, PscHaulUnit unit, QuickSearchFilter search)
        {
            if (settings == null || !unit.IsValid) return;

            var btn = PscUiWidgets.EntryButtonRect();
            float y = btn.y + (btn.height - IconSize) / 2f;
            float x = btn.xMax + StripGap;

            var data = PscStorageDataStore.TryGet(settings);

            // Link gating mirrors PscFeederGizmos: the only-from / only-to toggles are unavailable
            // until a source / destination route exists for this unit.
            var psc = PscMapComponent.For(unit.Map);
            string id = unit.UniqueLoadID;
            bool hasSource = psc != null && id != null && psc.Links.HasAnySource(id);
            bool hasDest = psc != null && id != null && psc.Links.HasAnyDestination(id);

            // 1. Batch fill
            int batch = data?.batch ?? 0;
            if (DrawToggle(NextRect(ref x, y), BatchInTex, batch > 0, disabled: false,
                    BatchTip("PSC_BatchFill", "PSC_BatchFillTip", "PSC_Toggle_BatchInHint", batch),
                    onRight: () => OpenControlWindow(settings, unit, search)))
                ToggleBatch(settings, fill: true);

            // 2. Batch empty
            int batchEmpty = data?.batchEmpty ?? 0;
            if (DrawToggle(NextRect(ref x, y), BatchOutTex, batchEmpty > 0, disabled: false,
                    BatchTip("PSC_BatchEmpty", "PSC_BatchEmptyTip", "PSC_Toggle_BatchOutHint", batchEmpty),
                    onRight: () => OpenControlWindow(settings, unit, search)))
                ToggleBatch(settings, fill: false);

            // 3. Pull only from sources
            bool onlyFrom = data?.onlyFromSource ?? false;
            if (DrawToggle(NextRect(ref x, y), OnlyFromTex, onlyFrom, disabled: !hasSource,
                    FlagTip("PSC_OnlyFromSource", "PSC_OnlyFromSourceDesc", onlyFrom, !hasSource, "PSC_NoSourceReason"),
                    onRight: null))
                ToggleFlag(settings, fromSource: true);

            // 4. Push only to destinations
            bool onlyTo = data?.onlyToDestinations ?? false;
            if (DrawToggle(NextRect(ref x, y), OnlyToTex, onlyTo, disabled: !hasDest,
                    FlagTip("PSC_OnlyToDestinations", "PSC_OnlyToDestinationsDesc", onlyTo, !hasDest, "PSC_NoDestinationReason"),
                    onRight: null))
                ToggleFlag(settings, fromSource: false);

            // 5. Alarm
            bool armed = data?.alarm?.IsActive ?? false;
            if (DrawToggle(NextRect(ref x, y), AlarmTex, armed, disabled: false,
                    AlarmTip(armed),
                    onRight: () => Find.WindowStack.Add(new Dialog_PscAlarm(settings, unit))))
                ToggleAlarm(settings);
        }

        private static Rect NextRect(ref float x, float y)
        {
            var r = new Rect(x, y, IconSize, IconSize);
            x += IconSize + IconGap;
            return r;
        }

        // Returns true on a left-click (the toggle). Right-click fires onRight and is swallowed.
        // A disabled icon draws dim, shows its (reason-bearing) tooltip, and ignores clicks.
        private static bool DrawToggle(Rect rect, Texture2D tex, bool isOn, bool disabled, string tooltip, Action onRight)
        {
            if (isOn && !disabled) Widgets.DrawHighlightSelected(rect);
            if (!disabled && Mouse.IsOver(rect)) Widgets.DrawHighlight(rect);

            var prev = GUI.color;
            GUI.color = disabled ? DisabledIcon : (isOn ? Color.white : OffIcon);
            GUI.DrawTexture(rect.ContractedBy(2f), tex, ScaleMode.ScaleToFit);
            GUI.color = prev;

            if (!tooltip.NullOrEmpty()) TooltipHandler.TipRegion(rect, tooltip);

            if (disabled) return false;

            if (onRight != null && Event.current.type == EventType.MouseDown && Event.current.button == 1
                && rect.Contains(Event.current.mousePosition))
            {
                onRight();
                SoundDefOf.Click.PlayOneShotOnCamera();
                Event.current.Use();
                return false;
            }

            return Widgets.ButtonInvisible(rect);
        }

        // --- state writes (mirror PscFeederGizmos / PscAlarmGizmo / PscControlWindow) ---

        private static void ToggleBatch(StorageSettings settings, bool fill)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            if (data == null) return;
            if (fill) data.batch = data.batch > 0 ? 0 : DefaultBatchSize;
            else data.batchEmpty = data.batchEmpty > 0 ? 0 : DefaultBatchSize;
            PscMapComponent.NotifyPolicyChanged(settings);
            SoundDefOf.Click.PlayOneShotOnCamera();
        }

        private static void ToggleFlag(StorageSettings settings, bool fromSource)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            if (data == null) return;
            if (fromSource) data.onlyFromSource = !data.onlyFromSource;
            else data.onlyToDestinations = !data.onlyToDestinations;
            PscMapComponent.NotifyPolicyChanged(settings);
            SoundDefOf.Click.PlayOneShotOnCamera();
        }

        private static void ToggleAlarm(StorageSettings settings)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            if (data == null) return;
            if (data.alarm?.IsActive == true)
            {
                data.alarm = null;
            }
            else
            {
                var cfg = data.alarm ?? new PscAlarmConfig { repeat = PscAlarmRepeat.Daily };
                cfg.highPct = DefaultHighPct;
                cfg.highSinceTick = cfg.highFiredTick = -1;
                data.alarm = cfg;
            }
            PscMapComponent.NotifyPolicyChanged(settings);
            SoundDefOf.Click.PlayOneShotOnCamera();
        }

        private static void OpenControlWindow(StorageSettings settings, PscHaulUnit unit, QuickSearchFilter search)
        {
            var open = Find.WindowStack.WindowOfType<PscControlWindow>();
            if (open == null) Find.WindowStack.Add(new PscControlWindow(settings, unit, search));
            else if (!ReferenceEquals(open.Settings, settings)) open.Retarget(settings, unit, search);
        }

        // --- tooltips ---

        private static string BatchTip(string labelKey, string descKey, string hintKey, int value)
        {
            string state = value > 0 ? "PSC_Toggle_BatchValue".Translate(value).ToString() : "PSC_Toggle_StateOff".Translate().ToString();
            return labelKey.Translate() + " — " + state + "\n\n" + descKey.Translate() + "\n\n" + hintKey.Translate(DefaultBatchSize);
        }

        private static string FlagTip(string labelKey, string descKey, bool on, bool disabled, string reasonKey)
        {
            string state = (on ? "PSC_Toggle_StateOn" : "PSC_Toggle_StateOff").Translate().ToString();
            string tip = labelKey.Translate() + " — " + state + "\n\n" + descKey.Translate();
            if (disabled) tip += "\n\n" + reasonKey.Translate();
            return tip;
        }

        private static string AlarmTip(bool armed)
        {
            string state = (armed ? "PSC_Toggle_StateOn" : "PSC_Toggle_StateOff").Translate().ToString();
            return "PSC_Alarm_GizmoLabel".Translate() + " — " + state + "\n\n" + "PSC_Toggle_AlarmHint".Translate(DefaultHighPct);
        }
    }
}
