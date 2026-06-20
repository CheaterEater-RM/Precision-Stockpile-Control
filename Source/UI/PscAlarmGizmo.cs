using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace PrecisionStockpileControl
{
    // The stockpile-alarm gizmo shown on a selected stockpile / shelf. One button:
    //   left-click  → opens the alarm config dialog (Dialog_PscAlarm).
    //   right-click → "Disarm alarm (this storage)" and "Disarm all alarms (this map)" (confirmed).
    // The icon is tinted to reflect armed/disarmed state so the player sees status at a glance.
    // Appended to the GetGizmos postfixes alongside the mode + feeder gizmos.
    [StaticConstructorOnStartup]
    public static class PscAlarmGizmo
    {
        private static readonly Texture2D AlarmTex = Load("UI/Alarm/Alarm");
        private static readonly Color ArmedColor = new Color(1f, 0.82f, 0.16f);   // amber when armed
        private static readonly Color DisarmedColor = new Color(0.65f, 0.65f, 0.65f);

        private static Texture2D Load(string path) => ContentFinder<Texture2D>.Get(path, reportFailure: false) ?? BaseContent.BadTex;

        public static IEnumerable<Gizmo> GizmosFor(StorageSettings settings, PscHaulUnit unit)
        {
            if (settings == null || !unit.IsValid) yield break;
            bool armed = PscStorageDataStore.TryGet(settings)?.alarm?.IsActive ?? false;

            yield return new Command_PscAlarm
            {
                icon = AlarmTex,
                defaultLabel = "PSC_Alarm_GizmoLabel".Translate(),
                defaultDesc = "PSC_Alarm_GizmoDesc".Translate(),
                defaultIconColor = armed ? ArmedColor : DisarmedColor,
                settings = settings,
                unit = unit,
                action = () => Find.WindowStack.Add(new Dialog_PscAlarm(settings, unit))
            };
        }

        // Right-click quick-arm preset. Sets one side to a default threshold without opening the
        // dialog; keeps the other side and any existing repeat/notify. Re-arms that side's runtime.
        private const int DefaultHighPct = 90;
        private const int DefaultLowPct = 10;

        private static void ArmSide(StorageSettings settings, bool high, int pct)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            var cfg = data.alarm ?? new PscAlarmConfig { repeat = PscAlarmRepeat.Daily };
            if (high) { cfg.highPct = pct; cfg.highSinceTick = cfg.highFiredTick = -1; }
            else { cfg.lowPct = pct; cfg.lowSinceTick = cfg.lowFiredTick = -1; }
            data.alarm = cfg;
            PscMapComponent.NotifyPolicyChanged(settings);
            SoundDefOf.Click.PlayOneShotOnCamera();
        }

        private static void DisarmThis(StorageSettings settings)
        {
            var data = PscStorageDataStore.TryGet(settings);
            if (data?.alarm == null) return;
            data.alarm = null;
            PscMapComponent.NotifyPolicyChanged(settings);
            SoundDefOf.Click.PlayOneShotOnCamera();
        }

        // Left-click opens the dialog (base Command_Action.action). The disarm actions live in the
        // right-click float menu; the map-wide disarm is gated behind a confirmation dialog, mirroring
        // the feeder's global "clear all routes".
        private class Command_PscAlarm : Command_Action
        {
            public StorageSettings settings;
            public PscHaulUnit unit;

            public override IEnumerable<FloatMenuOption> RightClickFloatMenuOptions
            {
                get
                {
                    yield return new FloatMenuOption("PSC_Alarm_ArmHigh".Translate(DefaultHighPct),
                        () => ArmSide(settings, high: true, DefaultHighPct));
                    yield return new FloatMenuOption("PSC_Alarm_ArmLow".Translate(DefaultLowPct),
                        () => ArmSide(settings, high: false, DefaultLowPct));
                    yield return new FloatMenuOption("PSC_Alarm_DisarmThis".Translate(), () => DisarmThis(settings));
                    yield return new FloatMenuOption("PSC_Alarm_DisarmAll".Translate(), () =>
                    {
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                            "PSC_Alarm_DisarmAllConfirm".Translate(),
                            () =>
                            {
                                PscMapComponent.For(unit.Map)?.DisarmAllAlarms();
                                SoundDefOf.Click.PlayOneShotOnCamera();
                            },
                            destructive: true));
                    });
                }
            }
        }
    }
}
