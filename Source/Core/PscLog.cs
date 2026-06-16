using System.Collections.Generic;
using Verse;

namespace PrecisionStockpileControl
{
    // Dev-mode diagnostic logging. Off by default. When disabled the only cost is one static bool
    // read; hot call sites additionally guard with `if (PscLog.Enabled)` so no string is built. The
    // guard AT the call site (not just inside Msg) is what avoids the interpolation/allocation.
    //
    // Enabled mirrors PscSettings.debugLogging, synced on mod load (PscMod ctor) and on the settings
    // toggle. Messages are prefixed "[PSC]" (matching the existing error logs) and tagged by
    // subsystem ("link:", "feeder:", "order:") so a dev can grep one stream out of the combined log.
    public static class PscLog
    {
        public static bool Enabled;

        public static void Msg(string msg)  { if (Enabled) Log.Message("[PSC] " + msg); }
        public static void Warn(string msg) { if (Enabled) Log.Warning("[PSC] " + msg); }

        // Dedup-throttle for per-haul-scan sites: the same `key` is logged at most once per
        // ThrottleTicks, so an identical decision repeated every scan collapses to one line. The
        // map is transient (never scribed) and only touched on the main thread while logging is on.
        private const int ThrottleTicks = 250;   // ~4s at 1x; tunable
        private const int MaxKeys = 512;          // overflow guard so the map can't grow unbounded
        private static readonly Dictionary<string, int> lastTick = new Dictionary<string, int>();

        public static void MsgThrottled(string key, string msg)
        {
            if (!Enabled) return;
            int now = Find.TickManager?.TicksGame ?? 0;
            // Suppress only a recent repeat; `now >= t` lets a tick reset (save reload) re-log.
            if (lastTick.TryGetValue(key, out int t) && now >= t && now - t < ThrottleTicks) return;
            if (lastTick.Count > MaxKeys) lastTick.Clear();
            lastTick[key] = now;
            Log.Message("[PSC] " + msg);
        }
    }
}
