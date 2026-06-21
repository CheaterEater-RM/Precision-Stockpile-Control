using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Mod settings. Every toggle below is a live feature: feeder strictness defaults + auto-priority,
    // 1-10 priority numbering + reverse, storage-button visibility, and dev-mode diagnostic logging.
    public class PscSettings : ModSettings
    {
        public bool autosetSourcePriority = false;      // D4 — Connect-source: step the painted source DOWN one letter (off by default)
        public bool autosetDestinationPriority = false; // D4 — Connect-destination: step the painted destination UP one letter (off by default)
        public bool defaultOnlyFromSource = true;     // M3 — seed strictness on first source link
        public bool defaultOnlyToDestinations = true; // M3 — seed strictness on first destination link
        public bool priorityNumbering = false;        // M4 — show 1-10 levels (two sub-tiers per band)
        public bool reverseOrder = false;             // M4 — 1-10 label flip only (ordering unchanged)
        public bool debugLogging = false;             // dev-mode diagnostic logging (PscLog)

        // Hide the PSC button on single-purpose containers (bookcases, graves, outfit stands,
        // feedstock vats, mortar shells, gene banks) where priority/limits are meaningless.
        public bool hideButtonOnSpecialStorage = true;
        // Extra storage defNames to hide the button on (e.g. modded single-purpose storage). One
        // exact, case-sensitive defName per entry; edited as one-per-line text in the settings panel.
        public List<string> extraExcludedDefNames = new List<string>();

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref autosetSourcePriority, "autosetSourcePriority", false);
            Scribe_Values.Look(ref autosetDestinationPriority, "autosetDestinationPriority", false);
            Scribe_Values.Look(ref defaultOnlyFromSource, "defaultOnlyFromSource", true);
            Scribe_Values.Look(ref defaultOnlyToDestinations, "defaultOnlyToDestinations", true);
            Scribe_Values.Look(ref priorityNumbering, "priorityNumbering", false);
            Scribe_Values.Look(ref reverseOrder, "reverseOrder", false);
            Scribe_Values.Look(ref debugLogging, "debugLogging", false);
            Scribe_Values.Look(ref hideButtonOnSpecialStorage, "hideButtonOnSpecialStorage", true);
            Scribe_Collections.Look(ref extraExcludedDefNames, "extraExcludedDefNames", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars && extraExcludedDefNames == null)
                extraExcludedDefNames = new List<string>();
        }
    }

    public class PscMod : Mod
    {
        public static PscSettings Settings { get; private set; }

        // Editable mirror of Settings.extraExcludedDefNames as one-per-line text. Seeded lazily on
        // first draw; parsed back into the list whenever it changes.
        private string excludedDefNamesBuffer;

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

            // Current-feature summary + quick-start, so the panel explains itself on first open.
            Text.Font = GameFont.Tiny;
            GUI.color = PscUiTheme.NoteText;
            listing.Label("PSC_SettingsIntro".Translate());
            listing.Label("PSC_SettingsQuickStart".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            listing.GapLine();

            listing.Label("PSC_SettingsFeederHeader".Translate());
            listing.Gap(6f);
            listing.CheckboxLabeled("PSC_SettingsDefaultOnlyFromSource".Translate(), ref Settings.defaultOnlyFromSource,
                "PSC_SettingsDefaultOnlyFromSourceTip".Translate());
            listing.CheckboxLabeled("PSC_SettingsDefaultOnlyToDestinations".Translate(), ref Settings.defaultOnlyToDestinations,
                "PSC_SettingsDefaultOnlyToDestinationsTip".Translate());
            listing.CheckboxLabeled("PSC_SettingsAutoPrioritySource".Translate(), ref Settings.autosetSourcePriority,
                "PSC_SettingsAutoPrioritySourceTip".Translate());
            listing.CheckboxLabeled("PSC_SettingsAutoPriorityDestination".Translate(), ref Settings.autosetDestinationPriority,
                "PSC_SettingsAutoPriorityDestinationTip".Translate());

            listing.Gap(12f);
            listing.Label("PSC_SettingsFineOrderHeader".Translate());
            listing.Gap(6f);
            // Reverse-aware legend: DisplayLevel(1)/(10) give the displayed numbers for the highest
            // and lowest levels, so it stays correct when "Reverse 1-10 numbering" is on.
            Text.Font = GameFont.Tiny;
            GUI.color = PscUiTheme.NoteText;
            listing.Label("PSC_FineOrderLegend".Translate(PscOrder.DisplayLevel(1), PscOrder.DisplayLevel(10)));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            // Toggling 1-10 changes whether sub-tier participates in ordering — re-sort every map's
            // haul-destination list so the change takes effect immediately.
            bool prevNumbering = Settings.priorityNumbering;
            listing.CheckboxLabeled("PSC_SettingsPriorityNumbering".Translate(), ref Settings.priorityNumbering,
                "PSC_SettingsPriorityNumberingTip".Translate());
            if (Settings.priorityNumbering != prevNumbering) ResortAllMaps();
            listing.CheckboxLabeled("PSC_SettingsReverseOrder".Translate(), ref Settings.reverseOrder,
                "PSC_SettingsReverseOrderTip".Translate());

            listing.Gap(12f);
            listing.Label("PSC_SettingsStorageButtonHeader".Translate());
            listing.Gap(6f);
            listing.CheckboxLabeled("PSC_SettingsHideButtonSpecial".Translate(), ref Settings.hideButtonOnSpecialStorage,
                "PSC_SettingsHideButtonSpecialTip".Translate());
            Text.Font = GameFont.Tiny;
            GUI.color = PscUiTheme.NoteText;
            listing.Label("PSC_SettingsExtraExcludedLabel".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            if (excludedDefNamesBuffer == null)
                excludedDefNamesBuffer = string.Join("\n", Settings.extraExcludedDefNames);
            string editedExcluded = Widgets.TextArea(listing.GetRect(72f), excludedDefNamesBuffer);
            if (editedExcluded != excludedDefNamesBuffer)
            {
                excludedDefNamesBuffer = editedExcluded;
                Settings.extraExcludedDefNames = editedExcluded
                    .Split('\n')
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
            }

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
