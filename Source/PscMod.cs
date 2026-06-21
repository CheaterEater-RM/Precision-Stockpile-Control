using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Mod settings. Every toggle below is a live feature: feeder strictness defaults + auto-priority,
    // 1-10 priority numbering + reverse, overlay rendering, and dev-mode diagnostic logging.
    public class PscSettings : ModSettings
    {
        public bool autosetSourcePriority = false;      // D4 — Connect-source: step the painted source DOWN one letter (off by default)
        public bool autosetDestinationPriority = false; // D4 — Connect-destination: step the painted destination UP one letter (off by default)
        public bool defaultOnlyFromSource = true;     // M3 — seed strictness on first source link
        public bool defaultOnlyToDestinations = true; // M3 — seed strictness on first destination link
        public bool priorityNumbering = false;        // M4 — show 1-10 levels (two sub-tiers per band)
        public bool reverseOrder = false;             // M4 — 1-10 label flip only (ordering unchanged)
        public bool feederPortSpreading = true;       // overlay: fan route endpoints along the storage perimeter (declutter)
        public bool feederFocusDim = true;            // overlay: dim routes not touching the hovered/selected storage
        public bool feederFlowDots = false;           // overlay: animate flow dots on the hovered/selected pile's valid routes
        public float feederLineWidth = 0.06f;         // overlay: route line thickness (arrows/✕ scale with it); dev-tunable
        public bool debugLogging = false;             // dev-mode diagnostic logging (PscLog)

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref autosetSourcePriority, "autosetSourcePriority", false);
            Scribe_Values.Look(ref autosetDestinationPriority, "autosetDestinationPriority", false);
            Scribe_Values.Look(ref defaultOnlyFromSource, "defaultOnlyFromSource", true);
            Scribe_Values.Look(ref defaultOnlyToDestinations, "defaultOnlyToDestinations", true);
            Scribe_Values.Look(ref priorityNumbering, "priorityNumbering", false);
            Scribe_Values.Look(ref reverseOrder, "reverseOrder", false);
            Scribe_Values.Look(ref feederPortSpreading, "feederPortSpreading", true);
            Scribe_Values.Look(ref feederFocusDim, "feederFocusDim", true);
            Scribe_Values.Look(ref feederFlowDots, "feederFlowDots", false);
            Scribe_Values.Look(ref feederLineWidth, "feederLineWidth", 0.06f);
            Scribe_Values.Look(ref debugLogging, "debugLogging", false);
        }
    }

    public class PscMod : Mod
    {
        public static PscSettings Settings { get; private set; }

        // Scroll state for the settings window (content can exceed the window height).
        private Vector2 settingsScroll;
        private float settingsHeight = 600f;

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
            var view = new Rect(0f, 0f, inRect.width - 20f, settingsHeight);
            Widgets.BeginScrollView(inRect, ref settingsScroll, view);
            var listing = new Listing_Standard();
            listing.Begin(view);

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

            // Developer-only diagnostic logging. Hidden outside RimWorld dev mode so it never clutters
            // a normal player's settings; logs gate purely on the setting (dev mode only controls
            // visibility), so a player who turns it on can leave dev mode and still capture a trace.
            if (Prefs.DevMode)
            {
                // Feeder-overlay rendering toggles. Shipped ON for everyone (the declutter is the
                // intended look); exposed only in dev mode so the normal panel stays uncluttered while
                // these are tuned in-game.
                listing.Gap(12f);
                listing.Label("PSC_SettingsOverlayHeader".Translate());
                listing.Gap(6f);
                listing.CheckboxLabeled("PSC_SettingsPortSpreading".Translate(), ref Settings.feederPortSpreading,
                    "PSC_SettingsPortSpreadingTip".Translate());
                listing.CheckboxLabeled("PSC_SettingsFocusDim".Translate(), ref Settings.feederFocusDim,
                    "PSC_SettingsFocusDimTip".Translate());
                listing.CheckboxLabeled("PSC_SettingsFlowDots".Translate(), ref Settings.feederFlowDots,
                    "PSC_SettingsFlowDotsTip".Translate());
                listing.Label("PSC_SettingsLineWidth".Translate(Settings.feederLineWidth.ToString("0.000")));
                Settings.feederLineWidth = listing.Slider(Settings.feederLineWidth, 0.02f, 0.16f);

                listing.Gap(12f);
                listing.Label("PSC_SettingsDebugHeader".Translate());
                listing.Gap(6f);
                listing.CheckboxLabeled("PSC_SettingsDebugLogging".Translate(), ref Settings.debugLogging,
                    "PSC_SettingsDebugLoggingTip".Translate());
                PscLog.Enabled = Settings.debugLogging;   // keep the cached gate in sync with the toggle
            }

            listing.End();
            settingsHeight = listing.CurHeight;   // self-size the scroll view for next frame
            Widgets.EndScrollView();
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
