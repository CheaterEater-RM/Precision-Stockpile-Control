using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // Mirrors how the vanilla storage tree reveals items under a search, so the PSC window's
    // "Apply to search" set matches what the tree shows. A def matches when the search hits:
    //   * its label            (vanilla QuickSearchFilter.Matches(ThingDef) behaviour), OR
    //   * its defName          (RimWorld uses Category_Variant defNames: Meat_Cow, Leather_Bird —
    //                           this is the "match by def" trick Material Filter uses), OR
    //   * any category label up the tree (semantic "is-a": beef is in the "meat" category; this is
    //                           what vanilla's DoCategory subtreeMatchedSearch effectively does).
    public static class PscSearchMatch
    {
        public static bool Matches(QuickSearchFilter search, ThingDef def)
        {
            if (search == null || !search.Active) return true;
            if (def == null) return false;
            // Don't reveal undiscovered items via the broader match (mirror vanilla Matches(ThingDef)).
            if (Find.HiddenItemsManager.Hidden(def)) return false;

            if (search.Matches(def.label)) return true;
            if (search.Matches(def.defName)) return true;

            var cats = def.thingCategories;
            if (cats != null)
            {
                for (int i = 0; i < cats.Count; i++)
                    for (var c = cats[i]; c != null; c = c.parent)
                        if (search.Matches(c.label)) return true;
            }
            return false;
        }
    }
}
