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
        public bool feederPortSpreading = false;      // overlay: fan route endpoints along the storage perimeter (declutter)
        public bool feederFocusDim = true;            // overlay: dim routes not touching the focused (selected/hovered) chain
        public bool feederFlowDots = true;            // overlay: animate flow dots on the focused pile's valid routes
        public bool feederDirectionColor = true;      // overlay: colour outgoing routes amber, incoming green (red = invalid)
        public bool feederChainHighlight = true;      // overlay: light up the whole up/downstream chain from the focus, fading per hop
        public bool feederHashShading = true;         // overlay: nudge each line's lightness so dense bundles separate
        public bool feederDotsOnly = true;            // overlay: replace arrows with flowing dots on every valid route (sub-mode of feederFlowDots)
        public float feederLineWidth = 0.04f;         // overlay: route line thickness (arrows/✕ scale with it)
        public bool reservedFillCounting = true;      // count in-flight hauls toward a cap so concurrent haulers don't overshoot
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
            Scribe_Values.Look(ref feederPortSpreading, "feederPortSpreading", false);
            Scribe_Values.Look(ref feederFocusDim, "feederFocusDim", true);
            Scribe_Values.Look(ref feederFlowDots, "feederFlowDots", true);
            Scribe_Values.Look(ref feederDirectionColor, "feederDirectionColor", true);
            Scribe_Values.Look(ref feederChainHighlight, "feederChainHighlight", true);
            Scribe_Values.Look(ref feederHashShading, "feederHashShading", true);
            Scribe_Values.Look(ref feederDotsOnly, "feederDotsOnly", true);
            Scribe_Values.Look(ref feederLineWidth, "feederLineWidth", 0.04f);
            Scribe_Values.Look(ref reservedFillCounting, "reservedFillCounting", true);
            Scribe_Values.Look(ref debugLogging, "debugLogging", false);
        }

        // Restore every setting to its shipped default. Kept in lockstep with the field initialisers
        // above (and the Scribe defaults) — one place to change a default, one place to reset it. The
        // reset button on every settings tab calls this; it resets ALL settings, not just one tab's.
        public void ResetToDefaults()
        {
            autosetSourcePriority = false;
            autosetDestinationPriority = false;
            defaultOnlyFromSource = true;
            defaultOnlyToDestinations = true;
            priorityNumbering = false;
            reverseOrder = false;
            feederPortSpreading = false;
            feederFocusDim = true;
            feederFlowDots = true;
            feederDirectionColor = true;
            feederChainHighlight = true;
            feederHashShading = true;
            feederDotsOnly = true;
            feederLineWidth = 0.04f;
            reservedFillCounting = true;
            debugLogging = false;
        }
    }

    public class PscMod : Mod
    {
        public static PscSettings Settings { get; private set; }

        // The settings panel is a 2-pane side-tab layout: a vertical text nav on the left selects
        // which page of controls the scrolling content pane on the right shows.
        private enum SettingsTab { Welcome, General, Ui }

        // Nav order + label key per tab, kept together so the nav loop and the content switch can't
        // drift apart. Welcome is first and the default selection.
        private static readonly (SettingsTab tab, string labelKey)[] Tabs =
        {
            (SettingsTab.Welcome, "PSC_SettingsTabWelcome"),
            (SettingsTab.General, "PSC_SettingsTabGeneral"),
            (SettingsTab.Ui, "PSC_SettingsTabUI"),
        };

        // UI-only state on the Mod singleton: lives the whole session, never saved, never a leaky
        // static. Per-tab scroll so each page remembers its own position; per-tab height self-corrects.
        private SettingsTab currentTab = SettingsTab.Welcome;
        private readonly Vector2[] tabScroll = new Vector2[3];
        private readonly float[] tabHeight = { 300f, 300f, 300f };

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
            // The mod-settings window is a fixed 900×700, so the nav/content split is plain arithmetic
            // off inRect — no resizable-window clamp math needed.
            var navRect = new Rect(inRect.x, inRect.y, PscUiTheme.SettingsNavWidth, inRect.height);
            var contentRect = new Rect(navRect.xMax + PscUiTheme.SettingsNavGap, inRect.y,
                inRect.width - PscUiTheme.SettingsNavWidth - PscUiTheme.SettingsNavGap, inRect.height);

            DrawNav(navRect);

            var prevColor = GUI.color;
            GUI.color = PscUiTheme.SettingsNavDivider;
            Widgets.DrawLineVertical(navRect.xMax + PscUiTheme.SettingsNavGap / 2f, inRect.y, inRect.height);
            GUI.color = prevColor;

            DrawContent(contentRect);
        }

        // Left nav: one clickable text row per tab; selected/hovered rows get a faint backing box.
        private void DrawNav(Rect rect)
        {
            float y = rect.y;
            var prevAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleLeft;
            foreach (var (tab, labelKey) in Tabs)
            {
                var row = new Rect(rect.x, y, rect.width, PscUiTheme.SettingsNavRowHeight);
                if (currentTab == tab) Widgets.DrawBoxSolid(row, PscUiTheme.SettingsNavSelected);
                else if (Mouse.IsOver(row)) Widgets.DrawBoxSolid(row, PscUiTheme.SettingsNavHover);

                var labelRect = new Rect(row.x + PscUiTheme.SettingsNavLabelInset, row.y,
                    row.width - PscUiTheme.SettingsNavLabelInset, row.height);
                Widgets.Label(labelRect, labelKey.Translate());

                if (Widgets.ButtonInvisible(row))
                {
                    currentTab = tab;
                    tabScroll[(int)tab] = Vector2.zero;   // open each freshly-clicked tab at the top
                }
                y += PscUiTheme.SettingsNavRowHeight + PscUiTheme.SettingsNavRowGap;
            }
            Text.Anchor = prevAnchor;
        }

        // Right pane: a per-tab scroll view that self-sizes to its content, dispatched by tab. A fixed
        // reset strip is reserved at the very bottom of the pane (outside the scroll) so the "reset all"
        // button stays visible on every tab regardless of scroll position or content length.
        private void DrawContent(Rect rect)
        {
            var scrollRect = new Rect(rect.x, rect.y, rect.width,
                rect.height - PscUiTheme.SettingsResetStripHeight);
            var resetRect = new Rect(rect.x, scrollRect.yMax, rect.width,
                PscUiTheme.SettingsResetStripHeight);

            int idx = (int)currentTab;
            // View height is never smaller than the visible pane (and NaN-safe: Mathf.Max(NaN, h) == h),
            // so a bad self-measured height can't collapse Listing.Begin's BeginGroup clip to nothing.
            float viewH = Mathf.Max(tabHeight[idx], scrollRect.height);
            var view = new Rect(0f, 0f, scrollRect.width - 20f, viewH);
            Widgets.BeginScrollView(scrollRect, ref tabScroll[idx], view);
            var listing = new Listing_Standard();
            listing.Begin(view);

            switch (currentTab)
            {
                case SettingsTab.Welcome: DrawWelcomeTab(listing); break;
                case SettingsTab.General: DrawGeneralTab(listing); break;
                case SettingsTab.Ui: DrawUiTab(listing); break;
            }

            listing.End();
            float measured = listing.CurHeight;   // self-size for next frame; reject a degenerate value
            tabHeight[idx] = (float.IsNaN(measured) || measured < 0f) ? scrollRect.height : measured;
            Widgets.EndScrollView();

            DrawResetButton(resetRect);
        }

        // The shared "reset all settings" control, drawn on every tab. The tooltip spells out that it
        // restores ALL settings (every tab), not just the active page, so a player can't mistake it for
        // a per-tab reset. After resetting, re-sync the cached log gate and re-sort haul destinations in
        // case 1-10 numbering changed.
        private void DrawResetButton(Rect strip)
        {
            float btnW = Mathf.Min(PscUiTheme.SettingsResetButtonWidth, strip.width);
            var btnRect = new Rect(strip.x, strip.yMax - PscUiTheme.SettingsResetButtonHeight,
                btnW, PscUiTheme.SettingsResetButtonHeight);

            TooltipHandler.TipRegion(btnRect, "PSC_SettingsResetAllTip".Translate());
            if (Widgets.ButtonText(btnRect, "PSC_SettingsResetAll".Translate()))
            {
                Settings.ResetToDefaults();
                PscLog.Enabled = Settings.debugLogging;   // keep the cached gate in sync with the reset value
                ResortAllMaps();                          // 1-10 numbering may have flipped back to default
            }
        }

        // Welcome: a capability-first orientation page — what PSC can do and where to find each
        // thing — so the panel explains itself on first open. Hierarchy is font size + the muted
        // NoteText footer (no rich-text bold), matching the rest of PSC's UI.
        private void DrawWelcomeTab(Listing_Standard listing)
        {
            Text.Font = GameFont.Small;
            listing.Label("PSC_SettingsIntro".Translate());        // one-line hook
            listing.Gap(6f);
            listing.Label("PSC_SettingsQuickStart".Translate());   // one-line quick start
            listing.Gap(12f);
            listing.Label("PSC_WelcomeFeaturesHeader".Translate());
            listing.Gap(4f);
            // Draw each bullet as its own label (split the keyed \n list) rather than one large
            // multi-line label, mirroring how the other tabs draw and letting each line wrap on its own.
            foreach (var line in ((string)"PSC_WelcomeFeatures".Translate()).Split('\n'))
                listing.Label(line);
            listing.Gap(12f);
            Text.Font = GameFont.Tiny;
            GUI.color = PscUiTheme.NoteText;
            listing.Label("PSC_WelcomeMore".Translate());          // muted pointer to the other tabs
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        // General options: storage-route defaults + fine order, with dev-only logging pinned at the bottom.
        private void DrawGeneralTab(Listing_Standard listing)
        {
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
            listing.Label("PSC_SettingsHaulingHeader".Translate());
            listing.Gap(6f);
            // Counting in-flight hauls toward a cap changes which units are "effectively full", so refresh
            // every map's anyReservedActive gate (and clear stale reserved when turning it off).
            bool prevReserved = Settings.reservedFillCounting;
            listing.CheckboxLabeled("PSC_SettingsReservedFill".Translate(), ref Settings.reservedFillCounting,
                "PSC_SettingsReservedFillTip".Translate());
            if (Settings.reservedFillCounting != prevReserved) RecomputeReservedActiveAllMaps();

            // Developer-only diagnostic logging, pinned at the bottom of this page. Hidden outside dev
            // mode so it never clutters a normal player's settings; logs gate purely on the setting
            // (dev mode only controls visibility), so a player who turns it on can leave dev mode and
            // still capture a trace.
            if (Prefs.DevMode)
            {
                listing.Gap(12f);
                listing.Label("PSC_SettingsDebugHeader".Translate());
                listing.Gap(6f);
                listing.CheckboxLabeled("PSC_SettingsDebugLogging".Translate(), ref Settings.debugLogging,
                    "PSC_SettingsDebugLoggingTip".Translate());
                PscLog.Enabled = Settings.debugLogging;   // keep the cached gate in sync with the toggle
            }
        }

        // UI: feeder-overlay rendering. Player-facing — these tune how the on-map overlay looks.
        private void DrawUiTab(Listing_Standard listing)
        {
            listing.Label("PSC_SettingsOverlayHeader".Translate());
            listing.Gap(6f);
            listing.CheckboxLabeled("PSC_SettingsPortSpreading".Translate(), ref Settings.feederPortSpreading,
                "PSC_SettingsPortSpreadingTip".Translate());
            listing.CheckboxLabeled("PSC_SettingsFocusDim".Translate(), ref Settings.feederFocusDim,
                "PSC_SettingsFocusDimTip".Translate());
            listing.CheckboxLabeled("PSC_SettingsFlowDots".Translate(), ref Settings.feederFlowDots,
                "PSC_SettingsFlowDotsTip".Translate());
            // "No arrows" is a sub-mode of flow dots: greyed out (and forced off) unless flow dots are on.
            SubCheckboxLabeled(listing, "PSC_SettingsDotsOnly".Translate(), "PSC_SettingsDotsOnlyTip".Translate(),
                ref Settings.feederDotsOnly, disabled: !Settings.feederFlowDots);
            listing.CheckboxLabeled("PSC_SettingsChainHighlight".Translate(), ref Settings.feederChainHighlight,
                "PSC_SettingsChainHighlightTip".Translate());
            listing.CheckboxLabeled("PSC_SettingsDirectionColor".Translate(), ref Settings.feederDirectionColor,
                "PSC_SettingsDirectionColorTip".Translate());
            listing.CheckboxLabeled("PSC_SettingsHashShading".Translate(), ref Settings.feederHashShading,
                "PSC_SettingsHashShadingTip".Translate());
            listing.Label("PSC_SettingsLineWidth".Translate(Settings.feederLineWidth.ToString("0.000")));
            Settings.feederLineWidth = listing.Slider(Settings.feederLineWidth, 0.02f, 0.16f);
        }

        // An indented sub-option checkbox (with tooltip): greys out and can't be toggled while disabled,
        // and is forced off when disabled so a dependent flag can't linger on without its parent.
        private const float SubIndent = 18f;
        private static void SubCheckboxLabeled(Listing_Standard listing, string label, string tip, ref bool value, bool disabled)
        {
            if (disabled) value = false;
            float h = Text.CalcHeight(label, listing.ColumnWidth - SubIndent);
            Rect rect = listing.GetRect(h);
            rect.xMin += SubIndent;
            if (Mouse.IsOver(rect)) Widgets.DrawHighlight(rect);
            if (!tip.NullOrEmpty()) TooltipHandler.TipRegion(rect, tip);
            Widgets.CheckboxLabeled(rect, label, ref value, disabled);
            listing.Gap(listing.verticalSpacing);
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

        // Reserved-fill-counting toggled: refresh every map's anyReservedActive gate, and when turning
        // it OFF, drop all reserved-inbound so no stale effective read lingers past the toggle.
        private static void RecomputeReservedActiveAllMaps()
        {
            var maps = Current.Game?.Maps;
            if (maps == null) return;
            foreach (var map in maps)
                PscMapComponent.For(map)?.RefreshReservedActive();
        }
    }
}
