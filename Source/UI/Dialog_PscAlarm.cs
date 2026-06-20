using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Per-stockpile alarm config (opened from PscAlarmGizmo). Edits a working copy and commits to the
    // unit's PscStorageData.alarm on close. Layout (council-reviewed): live fullness readout on top so
    // the player calibrates thresholds, then the two alarm sides (what to watch), then the shared
    // behaviour (how to nag), then an optional custom message. Sustain/repeat/notify are shared across
    // high and low — one alarm = one behaviour for this unit.
    public class Dialog_PscAlarm : Window
    {
        private readonly StorageSettings settings;
        private readonly PscHaulUnit unit;

        private bool highOn;
        private int? highVal;
        private string highBuf = "";
        private bool lowOn;
        private int? lowVal;
        private string lowBuf = "";

        private int? sustainVal;
        private string sustainBuf = "";
        private PscAlarmRepeat repeat = PscAlarmRepeat.Daily;   // council: Daily default avoids silent-miss footgun
        private PscAlarmNotify notify = PscAlarmNotify.Message;

        private bool customOn;
        private string message = "";

        private static readonly PscAlarmRepeat[] AllRepeats =
            { PscAlarmRepeat.OneShot, PscAlarmRepeat.Daily, PscAlarmRepeat.Quadrum };
        private static readonly PscAlarmNotify[] AllNotify =
            { PscAlarmNotify.Message, PscAlarmNotify.Letter };

        public override Vector2 InitialSize => new Vector2(440f, 430f);

        public Dialog_PscAlarm(StorageSettings settings, PscHaulUnit unit)
        {
            this.settings = settings;
            this.unit = unit;

            var cfg = PscStorageDataStore.TryGet(settings)?.alarm;
            if (cfg != null)
            {
                highOn = cfg.highPct >= 0;
                highVal = highOn ? cfg.highPct : (int?)90;
                lowOn = cfg.lowPct >= 0;
                lowVal = lowOn ? cfg.lowPct : (int?)10;
                sustainVal = cfg.sustainHours > 0 ? cfg.sustainHours : (int?)null;
                repeat = cfg.repeat;
                notify = cfg.notify;
                message = cfg.message ?? "";
                customOn = !string.IsNullOrEmpty(cfg.message);
            }
            else
            {
                highVal = 90;
                lowVal = 10;
            }
            highBuf = highVal?.ToString() ?? "";
            lowBuf = lowVal?.ToString() ?? "";
            sustainBuf = sustainVal?.ToString() ?? "";

            doCloseX = true;
            draggable = true;
            closeOnClickedOutside = true;
            layer = WindowLayer.Super;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var pa = Text.Anchor;
            var list = new Listing_Standard();
            list.Begin(inRect);

            Text.Font = GameFont.Small;
            list.Label(unit.Label ?? "PSC_Alarm_FallbackLabel".Translate());
            list.GapLine();

            DrawFullnessBar(list);
            list.Gap(8f);

            DrawSide(list, "PSC_Alarm_HighLabel".Translate(), ref highOn, ref highVal, ref highBuf, 90);
            DrawSide(list, "PSC_Alarm_LowLabel".Translate(), ref lowOn, ref lowVal, ref lowBuf, 10);

            if (highOn && lowOn && highVal.HasValue && lowVal.HasValue && highVal.Value <= lowVal.Value)
            {
                GUI.color = new Color(1f, 0.5f, 0.4f);
                Text.Font = GameFont.Tiny;
                list.Label("PSC_Alarm_OverlapWarning".Translate());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }

            list.GapLine();

            // Sustain dwell.
            var sRow = list.GetRect(28f);
            Widgets.Label(new Rect(sRow.x, sRow.y + 3f, sRow.width - 96f, sRow.height), "PSC_Alarm_Sustain".Translate());
            var sField = new Rect(sRow.xMax - 92f, sRow.y + 1f, 40f, sRow.height - 2f);
            PscLimitEditor.DrawNullableField(sField, ref sustainVal, ref sustainBuf, 0, 240);
            Widgets.Label(new Rect(sField.xMax + 4f, sRow.y + 3f, 44f, sRow.height), "PSC_Alarm_Hours".Translate());
            TooltipHandler.TipRegion(sRow, "PSC_Alarm_SustainTip".Translate());

            DrawChoiceRow(list, "PSC_Alarm_Repeat".Translate(), RepeatLabel(repeat), OpenRepeatMenu);
            DrawChoiceRow(list, "PSC_Alarm_Notify".Translate(), NotifyLabel(notify), OpenNotifyMenu);

            list.GapLine();

            list.CheckboxLabeled("PSC_Alarm_CustomMessage".Translate(), ref customOn);
            if (customOn)
            {
                GUI.color = PscUiTheme.HintText;
                Text.Font = GameFont.Tiny;
                list.Label("PSC_Alarm_CustomHint".Translate());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                var mr = list.GetRect(46f);
                message = Widgets.TextArea(mr, message ?? "");
            }

            list.End();
            Text.Anchor = pa;
        }

        private void DrawFullnessBar(Listing_Standard list)
        {
            var bar = list.GetRect(24f);
            string label;
            if (unit.TryGetFullnessPct(out int pct))
            {
                Widgets.DrawBoxSolid(bar, new Color(0.16f, 0.16f, 0.16f));
                Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, bar.width * (pct / 100f), bar.height),
                    new Color(0.3f, 0.55f, 0.85f));
                label = "PSC_Alarm_CurrentFullness".Translate(pct);
            }
            else
            {
                Widgets.DrawBoxSolid(bar, new Color(0.16f, 0.16f, 0.16f));
                label = "PSC_Alarm_CurrentFullnessUnknown".Translate();
            }
            var a = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(bar, label);
            Text.Anchor = a;
        }

        private void DrawSide(Listing_Standard list, string label, ref bool on, ref int? val, ref string buf, int seed)
        {
            var row = list.GetRect(28f);
            bool prev = on;
            Widgets.Checkbox(row.x, row.y + 1f, ref on);
            Widgets.Label(new Rect(row.x + 32f, row.y + 3f, row.width - 32f - 66f, row.height), label);
            if (on && !prev && !val.HasValue) { val = seed; buf = seed.ToString(); }
            if (on)
            {
                var field = new Rect(row.xMax - 60f, row.y + 1f, 44f, row.height - 2f);
                PscLimitEditor.DrawNullableField(field, ref val, ref buf, 0, 100);
                Widgets.Label(new Rect(field.xMax + 3f, row.y + 3f, 14f, row.height), "%");
            }
        }

        private void DrawChoiceRow(Listing_Standard list, string label, string current, Action onClick)
        {
            var row = list.GetRect(30f);
            Widgets.Label(new Rect(row.x, row.y + 4f, row.width * 0.42f, row.height), label);
            if (Widgets.ButtonText(new Rect(row.x + row.width * 0.42f, row.y, row.width * 0.58f, row.height), current))
                onClick();
        }

        private void OpenRepeatMenu()
        {
            var opts = new List<FloatMenuOption>(AllRepeats.Length);
            foreach (var r in AllRepeats)
            {
                var captured = r;
                opts.Add(new FloatMenuOption(RepeatLabel(captured), () => repeat = captured));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private void OpenNotifyMenu()
        {
            var opts = new List<FloatMenuOption>(AllNotify.Length);
            foreach (var n in AllNotify)
            {
                var captured = n;
                opts.Add(new FloatMenuOption(NotifyLabel(captured), () => notify = captured));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private static string RepeatLabel(PscAlarmRepeat r) => ("PSC_Alarm_Repeat_" + r).Translate();
        private static string NotifyLabel(PscAlarmNotify n) => ("PSC_Alarm_Notify_" + n).Translate();

        public override void PreClose()
        {
            base.PreClose();
            Commit();
        }

        // Apply the working state to the unit's alarm config. Drops the config to null when neither
        // side is armed (write-nothing-when-empty). Resets edge/dwell state when thresholds or sustain
        // changed so edited settings re-evaluate from a clean slate.
        private void Commit()
        {
            int newHigh = highOn ? Mathf.Clamp(highVal ?? 90, 0, 100) : -1;
            int newLow = lowOn ? Mathf.Clamp(lowVal ?? 10, 0, 100) : -1;
            int newSustain = Mathf.Max(0, sustainVal ?? 0);
            string newMsg = (customOn && !string.IsNullOrWhiteSpace(message)) ? message.Trim() : null;

            var data = PscStorageDataStore.GetOrCreate(settings);
            var cfg = data.alarm ?? new PscAlarmConfig();

            bool thresholdsChanged = cfg.highPct != newHigh || cfg.lowPct != newLow || cfg.sustainHours != newSustain;

            cfg.highPct = newHigh;
            cfg.lowPct = newLow;
            cfg.sustainHours = newSustain;
            cfg.repeat = repeat;
            cfg.notify = notify;
            cfg.message = newMsg;
            if (thresholdsChanged) cfg.ResetRuntime();

            data.alarm = cfg.IsActive ? cfg : null;
            PscMapComponent.NotifyPolicyChanged(settings);
        }
    }
}
