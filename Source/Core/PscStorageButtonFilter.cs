using System;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Decides whether to suppress PSC's LIMITS / policy UI (per-row limit rows, the PSC entry button,
    // the quick-toggle strip, and the storage gizmos) for a given storage. It NO LONGER governs PSC's
    // fine-order priority controls: the 1-10 level box now draws on any ITab_Storage-family tab whose
    // vanilla IsPrioritySettingVisible is true (graves/sarcophagi), independent of this list.
    //
    // A count limit is meaningless on a single-purpose / automated container, so those keep their
    // limits UI hidden: graves/sarcophagi (one corpse), feedstock vats (growth vat / biosculpter pod),
    // and mortar shell holders. Of these only graves have a vanilla priority band, so only graves get
    // the priority-only path; the feedstock/shell tabs are IsPrioritySettingVisible => false in vanilla,
    // so no priority UI appears there by vanilla's own design (nothing to suppress or add).
    //
    // Bookcases (ITab_ContentsBooks) and gene banks (ITab_ContentsGenepackHolder) derive from
    // ITab_ContentsBase, NOT ITab_Storage, so PSC's FillTab tab seam never fires there — their old
    // entries were dead no-ops and are removed. Genuine bookcase / gene-bank control is future work
    // that needs a new contents-tab seam, not an entry in this limits filter.
    //
    // Keyed by TYPE rather than defName so modded subclasses of these containers are caught too. All
    // referenced types live in Assembly-CSharp regardless of which DLC is active, so the direct
    // typeof references load fine even with Odyssey/Biotech disabled (instances just never exist),
    // and IsInstanceOfType simply never matches when the type is unused.
    //
    // Reel's Expanded Storage and other Adaptive Storage Framework packs are deliberately NOT listed:
    // they are full multi-stack storage with a priority and settable limits (the vanilla storage
    // tab), so PSC's features genuinely apply — including their books-only "bookshelf", which (unlike
    // a vanilla Building_Bookcase) has a priority and a settable book count. To exclude a specific
    // modded storage, patch its ThingDef with the PscStorageExclusion mod extension (XML-only; there
    // is no in-menu blacklist). The built-in type list below is definitive and always applies.
    public static class PscStorageButtonFilter
    {
        private static readonly Type[] BuiltinHiddenTypes =
        {
            typeof(Building_CorpseCasket),    // base of Building_Grave: graves + sarcophagi, one corpse
            typeof(Building_GrowthVat),       // nutrition feedstock, not a stockpile (Biotech)
            typeof(CompBiosculpterPod),       // nutrition feedstock (parent is the comp)
            typeof(CompChangeableProjectile), // mortar/turret loaded shells (parent is the comp)
        };

        public static bool ShouldHideLimits(IStoreSettingsParent parent)
        {
            if (parent == null) return false;

            // The building this parent will BECOME when built, for an unbuilt blueprint/frame; null for
            // already-built storage (whose own def/type is used directly). This makes a blacklisted
            // container's blueprint hide PSC exactly like the finished building.
            ThingDef builtDef = BuiltDef(parent);

            // Built-in single-purpose blacklist — definitive, always on. Match the parent instance
            // (built storage) OR, for a blueprint/frame, the class of the building it becomes.
            for (int i = 0; i < BuiltinHiddenTypes.Length; i++)
            {
                if (BuiltinHiddenTypes[i].IsInstanceOfType(parent)) return true;
                if (builtDef?.thingClass != null && BuiltinHiddenTypes[i].IsAssignableFrom(builtDef.thingClass))
                    return true;
            }

            // XML-only path: any ThingDef patched with the PscStorageExclusion mod extension. For a
            // blueprint/frame this is the built def, so the opt-out matches the finished building.
            ThingDef def = builtDef ?? ResolveDef(parent);
            return def != null && def.HasModExtension<PscStorageExclusion>();
        }

        // The storage tab's parent is the building itself for most storage, but a ThingComp for
        // comp-backed storage (mortar shells, biosculpter, gene banks). Zones / storage groups /
        // blueprints have no single owning def and are never excluded by defName.
        private static ThingDef ResolveDef(IStoreSettingsParent parent)
        {
            if (parent is Thing t) return t.def;
            if (parent is ThingComp c) return c.parent?.def;
            return null;
        }

        // For an unbuilt storage (Blueprint_Storage / storage Frame) the parent is a Thing whose def
        // has entityDefToBuild set to the real building ThingDef. Comp-backed containers key their
        // blacklist on a comp (not thingClass), so their unbuilt form is a documented minor edge.
        private static ThingDef BuiltDef(IStoreSettingsParent parent)
            => parent is Thing t && t.def?.entityDefToBuild is ThingDef built ? built : null;
    }
}
