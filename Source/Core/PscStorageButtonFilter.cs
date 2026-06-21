using System;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Decides whether to suppress PSC's storage-tab button + adjacent controls for a given storage.
    // PSC is opt-in and useless on single-purpose vanilla/DLC containers: bookcases (books only, no
    // priority, fixed capacity), graves/sarcophagi (one corpse), outfit stands (one apparel/weapon),
    // feedstock vats (growth vat / biosculpter pod), mortar shell holders, and gene banks.
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
            typeof(Building_Bookcase),        // books only, no priority, fixed capacity
            typeof(Building_CorpseCasket),    // base of Building_Grave: graves + sarcophagi, one corpse
            typeof(Building_GrowthVat),       // nutrition feedstock, not a stockpile (Biotech)
            typeof(CompBiosculpterPod),       // nutrition feedstock (parent is the comp)
            typeof(CompChangeableProjectile), // mortar/turret loaded shells (parent is the comp)
            typeof(CompGenepackContainer),    // gene bank (parent is the comp)
        };

        public static bool ShouldHide(IStoreSettingsParent parent)
        {
            if (parent == null) return false;

            // Built-in single-purpose blacklist — definitive, always on.
            for (int i = 0; i < BuiltinHiddenTypes.Length; i++)
                if (BuiltinHiddenTypes[i].IsInstanceOfType(parent))
                    return true;

            // XML-only path: any ThingDef patched with the PscStorageExclusion mod extension.
            ThingDef def = ResolveDef(parent);
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
    }
}
