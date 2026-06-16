using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Mod settings. M1 ships no behavioural toggles; the fields below are groundwork for later
    // milestones (M3 auto-priority, M4 subpriority linking) and are persisted now so the schema
    // is stable before those features land.
    public class PscSettings : ModSettings
    {
        public bool autosetSourcePriority = true;     // D4 — persisted; auto-priority still deferred
        public bool linkSubpriorities = false;        // M4 (§11.4)
        public bool defaultOnlyFromSource = true;     // M3 — seed strictness on first source link
        public bool defaultOnlyToDestinations = true; // M3 — seed strictness on first destination link
        public bool priorityNumbering = false;        // M4 — show 1-10 levels (two sub-tiers per band)
        public bool reverseOrder = false;             // M4 — 1-10 label flip only (ordering unchanged)
        public bool debugLogging = false;             // dev-mode diagnostic logging (PscLog)

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref autosetSourcePriority, "autosetSourcePriority", true);
            Scribe_Values.Look(ref linkSubpriorities, "linkSubpriorities", false);
            Scribe_Values.Look(ref defaultOnlyFromSource, "defaultOnlyFromSource", true);
            Scribe_Values.Look(ref defaultOnlyToDestinations, "defaultOnlyToDestinations", true);
            Scribe_Values.Look(ref priorityNumbering, "priorityNumbering", false);
            Scribe_Values.Look(ref reverseOrder, "reverseOrder", false);
            Scribe_Values.Look(ref debugLogging, "debugLogging", false);
        }
    }

    public class PscMod : Mod
    {
        public static PscSettings Settings { get; private set; }

        public PscMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<PscSettings>();
            PscLog.Enabled = Settings.debugLogging;   // seed the cached gate from the loaded setting
        }

        public override string SettingsCategory()
        {
            return "PSC_SettingsCategory".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label("PSC_SettingsFeederHeader".Translate());
            listing.Gap(6f);
            listing.CheckboxLabeled("PSC_SettingsDefaultOnlyFromSource".Translate(), ref Settings.defaultOnlyFromSource,
                "PSC_SettingsDefaultOnlyFromSourceTip".Translate());
            listing.CheckboxLabeled("PSC_SettingsDefaultOnlyToDestinations".Translate(), ref Settings.defaultOnlyToDestinations,
                "PSC_SettingsDefaultOnlyToDestinationsTip".Translate());

            listing.Gap(12f);
            listing.Label("PSC_SettingsFineOrderHeader".Translate());
            listing.Gap(6f);
            // Toggling 1-10 changes whether sub-tier participates in ordering — re-sort every map's
            // haul-destination list so the change takes effect immediately.
            bool prevNumbering = Settings.priorityNumbering;
            listing.CheckboxLabeled("PSC_SettingsPriorityNumbering".Translate(), ref Settings.priorityNumbering,
                "PSC_SettingsPriorityNumberingTip".Translate());
            if (Settings.priorityNumbering != prevNumbering) ResortAllMaps();
            listing.CheckboxLabeled("PSC_SettingsReverseOrder".Translate(), ref Settings.reverseOrder,
                "PSC_SettingsReverseOrderTip".Translate());

            // Developer-only diagnostic logging. Hidden outside RimWorld dev mode so it never clutters
            // a normal player's settings; logs gate purely on the setting (dev mode only controls
            // visibility), so a player who turns it on can leave dev mode and still capture a trace.
            if (Prefs.DevMode)
            {
                listing.Gap(12f);
                listing.Label("PSC_SettingsDebugHeader".Translate());
                listing.Gap(6f);
                listing.CheckboxLabeled("PSC_SettingsDebugLogging".Translate(), ref Settings.debugLogging,
                    "PSC_SettingsDebugLoggingTip".Translate());
                PscLog.Enabled = Settings.debugLogging;   // keep the cached gate in sync with the toggle
            }

            listing.End();
        }

        private static void ResortAllMaps()
        {
            var maps = Current.Game?.Maps;
            if (maps == null) return;
            foreach (var map in maps)
            {
                // Numbering toggled -> whether sub-tier participates in ordering changed; refresh the
                // gate so the fine-order transpiler isn't left armed for now-inert sub-tiers.
                PscMapComponent.For(map)?.RecomputeFineOrderActive();
                map.haulDestinationManager?.Notify_HaulDestinationChangedPriority();
            }
        }
    }
}
