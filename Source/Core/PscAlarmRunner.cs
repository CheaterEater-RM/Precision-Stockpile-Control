using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Evaluates a unit's alarm against current fullness and fires the notification. Kept separate from
    // PscAlarmConfig (plain data) and driven by the map-component tick (PscMapComponent.RunAlarmChecks),
    // so all the edge/dwell/cadence logic lives in one place.
    //
    // Per side (high/low): a `sinceTick` records when the unit entered the zone (sustain dwell), a
    // `firedTick` records the last fire (cadence + re-arm). Leaving the zone resets both — that's the
    // anti-transient + re-arm behaviour. Both are persisted on the config, so a save/reload neither
    // re-spams nor restarts the dwell.
    public static class PscAlarmRunner
    {
        // Returns true if a OneShot side fired and disabled itself this pass, so the caller refreshes
        // tracking/persistence via PscMapComponent.NotifyPolicyChanged.
        public static bool Evaluate(StorageSettings settings, PscStorageData data, PscHaulUnit unit, int now)
        {
            var cfg = data?.alarm;
            if (cfg == null || !cfg.IsActive || !unit.IsValid) return false;
            if (!unit.TryGetFullnessPct(out int pct)) return false;

            int sustainTicks = cfg.sustainHours > 0 ? cfg.sustainHours * GenDate.TicksPerHour : 0;
            bool selfDisabled = false;

            if (cfg.highPct >= 0
                && EvaluateSide(pct >= cfg.highPct, now, sustainTicks, cfg.repeat,
                    ref cfg.highSinceTick, ref cfg.highFiredTick))
            {
                Fire(unit, cfg, isHigh: true, pct);
                if (cfg.repeat == PscAlarmRepeat.OneShot)
                {
                    cfg.highPct = -1;
                    cfg.highSinceTick = cfg.highFiredTick = -1;
                    selfDisabled = true;
                }
            }

            if (cfg.lowPct >= 0
                && EvaluateSide(pct <= cfg.lowPct, now, sustainTicks, cfg.repeat,
                    ref cfg.lowSinceTick, ref cfg.lowFiredTick))
            {
                Fire(unit, cfg, isHigh: false, pct);
                if (cfg.repeat == PscAlarmRepeat.OneShot)
                {
                    cfg.lowPct = -1;
                    cfg.lowSinceTick = cfg.lowFiredTick = -1;
                    selfDisabled = true;
                }
            }

            return selfDisabled;
        }

        // Edge + sustain dwell + repeat cadence for one side. Mutates sinceTick/firedTick; returns
        // true exactly on the ticks a notification should fire.
        private static bool EvaluateSide(bool inZone, int now, int sustainTicks, PscAlarmRepeat repeat,
            ref int sinceTick, ref int firedTick)
        {
            if (!inZone) { sinceTick = -1; firedTick = -1; return false; }   // left zone -> re-arm
            if (sinceTick < 0) sinceTick = now;                             // just entered -> start dwell
            if (now - sinceTick < sustainTicks) return false;              // not sustained long enough
            if (firedTick < 0) { firedTick = now; return true; }           // first fire past the dwell
            switch (repeat)
            {
                case PscAlarmRepeat.Daily:
                    if (now - firedTick >= GenDate.TicksPerDay) { firedTick = now; return true; }
                    break;
                case PscAlarmRepeat.Quadrum:
                    if (now - firedTick >= GenDate.TicksPerQuadrum) { firedTick = now; return true; }
                    break;
            }
            return false;   // OneShot already fired, or cadence interval not yet elapsed
        }

        private static void Fire(PscHaulUnit unit, PscAlarmConfig cfg, bool isHigh, int pct)
        {
            string label = unit.Label;
            if (string.IsNullOrEmpty(label)) label = "PSC_Alarm_FallbackLabel".Translate();

            string text;
            if (!string.IsNullOrEmpty(cfg.message)) text = cfg.message;
            else if (isHigh) text = "PSC_Alarm_HighMsg".Translate(label, pct);
            else text = "PSC_Alarm_LowMsg".Translate(label, pct);

            LookTargets targets = TargetFor(unit);

            if (cfg.notify == PscAlarmNotify.Letter)
            {
                Find.LetterStack.ReceiveLetter("PSC_Alarm_LetterTitle".Translate(label), text,
                    LetterDefOf.NeutralEvent, targets);
            }
            else
            {
                Messages.Message(text, targets, MessageTypeDefOf.NeutralEvent);
            }
        }

        // Click-to-zoom target: the unit's draw-center cell. Null is acceptable (non-clickable toast).
        private static LookTargets TargetFor(PscHaulUnit unit)
        {
            var map = unit.Map;
            if (map != null && unit.TryGetDrawCenter(out var center))
                return new LookTargets(center.ToIntVec3(), map);
            return null;
        }
    }
}
