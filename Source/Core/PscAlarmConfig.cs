using Verse;

namespace PrecisionStockpileControl
{
    // Repeat cadence for a triggered alarm side. OneShot fires once on entry then disables that side
    // ("one-time then cancel"). Stored by Scribe_Values (writes the NAME) — reorder-safe, but never
    // rename a value (save-compat surface, AGENTS.md).
    public enum PscAlarmRepeat : byte
    {
        OneShot = 0,   // fire once, then cancel that side
        Daily = 1,     // re-fire at most once per game-day while in-zone
        Quadrum = 2,   // re-fire at most once per quadrum (season) while in-zone
    }

    // How a triggered alarm notifies the player. Message = transient on-screen toast (default);
    // Letter = persistent letter in the right-hand bar. Save-compat surface — never rename.
    public enum PscAlarmNotify : byte
    {
        Message = 0,
        Letter = 1,
    }

    // Per-storage alarm policy + its persisted runtime edge state, attached to PscStorageData and
    // riding the <psc> save node. Opt-in: a unit with no alarm holds a null config and costs nothing.
    //
    // Fullness is measured in OCCUPIED SLOTS (held stacks / total stack-slots), not items — see
    // PscHaulUnit.TryGetFullnessPct. Both sides (high/low) are independent and optional; the dwell,
    // cadence and notify style are shared across them (one alarm = one behaviour for this unit).
    //
    // Sentinel style mirrors PscDefLimit: int -1 = unset, never nullable, so Scribe_Values round-trips
    // boringly across versions.
    public class PscAlarmConfig : IExposable
    {
        // ---- Persistent policy ----
        public int highPct = -1;            // -1 disabled; 0-100: fire when full% >= highPct
        public int lowPct = -1;             // -1 disabled; 0-100: fire when full% <= lowPct
        public int sustainHours;            // 0 = fire immediately; N = require in-zone for N game-hours
        public PscAlarmRepeat repeat = PscAlarmRepeat.OneShot;
        public PscAlarmNotify notify = PscAlarmNotify.Message;
        public string message;              // null/empty = default text (includes the unit label)

        // ---- Runtime edge state (persisted so a save/reload neither re-spams nor restarts the dwell) ----
        // -1 means "armed / not currently in this side's zone".
        public int highSinceTick = -1;      // tick the high side first entered its zone
        public int lowSinceTick = -1;       // tick the low side first entered its zone
        public int highFiredTick = -1;      // tick the high side last fired
        public int lowFiredTick = -1;       // tick the low side last fired

        // Gates persistence + map-component tracking. A config that arms neither side is "no alarm".
        public bool IsActive => highPct >= 0 || lowPct >= 0;

        // Reset edge/dwell state. Called when thresholds or sustain change in the dialog so edited
        // settings re-evaluate from a clean slate instead of carrying stale sinceTick/firedTick.
        public void ResetRuntime()
        {
            highSinceTick = lowSinceTick = highFiredTick = lowFiredTick = -1;
        }

        public PscAlarmConfig Clone()
        {
            return new PscAlarmConfig
            {
                highPct = highPct,
                lowPct = lowPct,
                sustainHours = sustainHours,
                repeat = repeat,
                notify = notify,
                message = message,
                highSinceTick = highSinceTick,
                lowSinceTick = lowSinceTick,
                highFiredTick = highFiredTick,
                lowFiredTick = lowFiredTick,
            };
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref highPct, "highPct", -1);
            Scribe_Values.Look(ref lowPct, "lowPct", -1);
            Scribe_Values.Look(ref sustainHours, "sustainHours", 0);
            Scribe_Values.Look(ref repeat, "repeat", PscAlarmRepeat.OneShot);
            Scribe_Values.Look(ref notify, "notify", PscAlarmNotify.Message);
            Scribe_Values.Look(ref message, "message");
            Scribe_Values.Look(ref highSinceTick, "highSince", -1);
            Scribe_Values.Look(ref lowSinceTick, "lowSince", -1);
            Scribe_Values.Look(ref highFiredTick, "highFired", -1);
            Scribe_Values.Look(ref lowFiredTick, "lowFired", -1);
        }
    }
}
