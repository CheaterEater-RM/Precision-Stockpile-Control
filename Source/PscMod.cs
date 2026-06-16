using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Mod settings. M1 ships no behavioural toggles; the fields below are groundwork for later
    // milestones (M3 auto-priority, M4 subpriority linking) and are persisted now so the schema
    // is stable before those features land.
    public class PscSettings : ModSettings
    {
        public bool autosetSourcePriority = true;   // M3 (D4)
        public bool linkSubpriorities = false;       // M4 (§11.4)

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref autosetSourcePriority, "autosetSourcePriority", true);
            Scribe_Values.Look(ref linkSubpriorities, "linkSubpriorities", false);
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
            listing.Label("PSC_SettingsM1Note".Translate());
            listing.End();
        }
    }
}
