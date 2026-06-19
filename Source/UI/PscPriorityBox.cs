using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // The PSC fine-order controls drawn beside the vanilla Priority button on the storage tab
    // (design §10.7/§10.8). The vanilla button (group-space Rect(0,0,160,TopAreaHeight-6) inside a
    // BeginGroup contracted by 10) is left untouched; PSC draws to its right in window space.
    //
    //   - Letter box (always): a-z subpriority within the band. Click opens an a..z menu.
    //   - Level box (only when 1-10 numbering is on): the 1-10 priority level. Click opens a level
    //     menu; selecting a level sets the vanilla band AND the PSC sub-tier (the enum stays
    //     authoritative, D6).
    public static class PscPriorityBox
    {
        // Window-space geometry. Vanilla group origin is (10,10); the priority button is 160 wide and
        // (TopAreaHeight=35) - 6 = 29 tall.
        private const float RowX = 10f;
        private const float RowY = 10f;
        private const float RowH = 29f;
        private const float PriorityButtonW = 160f;
        private const float Gap = 6f;
        private const float LetterW = 40f;

        public static void Draw(StorageSettings settings)
        {
            if (settings == null) return;
            var unit = PscHaulUnit.ResolveSettings(settings);
            if (!unit.IsValid) return;

            var data = PscStorageDataStore.TryGet(settings);

            Text.Font = GameFont.Small;
            var prevAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleCenter;

            // With 1-10 numbering on, PSC subsumes the vanilla Priority band button: the suppression
            // patch hides vanilla's button (only on tabs that actually have one), and we draw the
            // level box in its exact footprint. The letter box then follows it. With numbering off,
            // the vanilla button stays and the letter box sits to its right (no level box).
            bool numbering = PscMod.Settings != null && PscMod.Settings.priorityNumbering;
            bool replaceVanilla = numbering && ITab_Storage_PrioritySuppress_Patch.LastPriorityVisibleOriginal;
            float x = RowX + PriorityButtonW + Gap;

            if (replaceVanilla)
            {
                var levelRect = new Rect(RowX, RowY, PriorityButtonW, RowH);
                int level = PscOrder.LevelFor(settings.Priority, data?.subTier ?? 0);
                if (Widgets.ButtonText(levelRect, "PSC_LevelBox".Translate(PscOrder.DisplayLevel(level))))
                    OpenLevelMenu(settings);
                TooltipHandler.TipRegion(levelRect, "PSC_LevelTip".Translate());
            }

            var letterRect = new Rect(x, RowY, LetterW, RowH);
            string letter = data?.letter;
            string letterLabel = string.IsNullOrEmpty(letter) ? "–" : letter.ToLowerInvariant();
            if (Widgets.ButtonText(letterRect, letterLabel))
                OpenLetterMenu(settings);
            TooltipHandler.TipRegion(letterRect, "PSC_LetterTip".Translate());

            Text.Anchor = prevAnchor;
        }

        private static void OpenLevelMenu(StorageSettings settings)
        {
            var options = new List<FloatMenuOption>();
            // Order by actual priority: lowest on top, highest on bottom — matching vanilla's band
            // dropdown (which iterates the enum Low..Critical). Level 1 is the highest priority
            // (Critical), level 10 the lowest (Low), so sort by level descending. The reverse-order
            // setting only flips the displayed number, not list position (PscOrder.DisplayLevel).
            var rows = new List<(int display, int level)>();
            for (int level = 1; level <= 10; level++)
                rows.Add((PscOrder.DisplayLevel(level), level));
            rows.Sort((p, q) => q.level.CompareTo(p.level));

            var data = PscStorageDataStore.TryGet(settings);
            int currentLevel = PscOrder.LevelFor(settings.Priority, data?.subTier ?? 0);
            foreach (var (display, level) in rows)
            {
                int captured = level;
                PscOrder.BandAndSubTierForLevel(level, out var band, out _);
                string label = "PSC_LevelOption".Translate(display, band.Label().CapitalizeFirst());
                if (level == currentLevel) label = "✓ " + label;
                options.Add(new FloatMenuOption(label, () => SetLevel(settings, captured)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void SetLevel(StorageSettings settings, int level)
        {
            PscOrder.BandAndSubTierForLevel(level, out var band, out var subTier);
            var data = PscStorageDataStore.GetOrCreate(settings);
            if (data == null) return;
            settings.Priority = band;       // keep the vanilla enum authoritative (D6)
            data.subTier = subTier;
            PscLog.Msg($"order: set level {level} -> band {band} subTier {subTier}");
            PscMapComponent.NotifyOrderChanged(settings);
        }

        private static void OpenLetterMenu(StorageSettings settings)
        {
            var data = PscStorageDataStore.TryGet(settings);
            string current = string.IsNullOrEmpty(data?.letter) ? null : data.letter.ToLowerInvariant();
            string Mark(string label, bool selected) => selected ? "✓ " + label : label;

            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption(Mark("PSC_LetterNone".Translate(), current == null),
                    () => SetLetter(settings, null))
            };
            for (char c = 'a'; c <= 'z'; c++)
            {
                string s = c.ToString();
                options.Add(new FloatMenuOption(Mark(s, current == s), () => SetLetter(settings, s)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void SetLetter(StorageSettings settings, string letter)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            if (data == null) return;
            data.letter = string.IsNullOrEmpty(letter) ? null : letter;
            PscLog.Msg($"order: set letter {(string.IsNullOrEmpty(letter) ? "(none)" : letter)}");
            PscMapComponent.NotifyOrderChanged(settings);
        }
    }
}
