using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Mod settings. M1 ships no behavioural toggles; the fields below are groundwork for later
    // milestones (M3 auto-priority, M4 subpriority linking) and are persisted now so the schema
    // is stable before those features land.
    public class PscSettings : ModSettings
    {
        public bool autosetSourcePriority = true;     // D4 — persisted now, applied with M4 fine-order
        public bool linkSubpriorities = false;        // M4 (§11.4)
        public bool defaultOnlyFromSource = true;     // M3 — seed strictness on first source link
        public bool defaultOnlyToDestinations = true; // M3 — seed strictness on first destination link

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref autosetSourcePriority, "autosetSourcePriority", true);
            Scribe_Values.Look(ref linkSubpriorities, "linkSubpriorities", false);
            Scribe_Values.Look(ref defaultOnlyFromSource, "defaultOnlyFromSource", true);
            Scribe_Values.Look(ref defaultOnlyToDestinations, "defaultOnlyToDestinations", true);
        }
    }

    public class PscMod : Mod
    {
        public static PscSettings Settings { get; private set; }

        public PscMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<PscSettings>();
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
            listing.End();
        }
    }
}
