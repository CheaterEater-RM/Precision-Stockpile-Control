using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // ── Vanilla reflection seams — version-migration checklist ─────────────────────────────────
    // Every place PSC reaches into a *non-public* or *overload-sensitive* vanilla member lives here,
    // so a RimWorld version bump has exactly ONE file to audit. When a member below is renamed or
    // removed, the resolver logs a single clear "[PSC] Vanilla reflection seam not found" error at
    // startup and the dependent feature degrades (returns a safe default) instead of crashing the
    // storage tab or spamming the log.
    //
    // Vanilla members relied upon (RimWorld 1.6):
    //   Listing.curY                          (private field)   — row-Y capture for storage filter rows
    //   Widgets.checkboxPainting              (private static)  — vanilla allow/disallow paint in progress
    //   Widgets.checkboxPaintingState         (private static)  — value being painted (true = allow)
    //   InspectTabBase.size                   (protected field) — the live storage-tab frame size (widen)
    //   ITab_Storage.SelStoreSettingsParent   (property getter) — the selected storage's settings parent
    //   ITab_Storage.thingFilterState.quickSearch.filter        — the tab's quick-search filter
    //   Widgets.Checkbox(float,float,bool&,float,bool,bool,Texture2D,Texture2D)  (overload)
    //   Widgets.CheckboxMulti(Rect,MultiCheckboxState,bool)                       (overload)
    //   PickUpAndHaul.WorkGiver_HaulToInventory:CapacityAt(Thing,IntVec3,Map)     (soft dependency)
    internal static class PscReflection
    {
        // ---- Listing.curY -------------------------------------------------------------------------
        private static readonly AccessTools.FieldRef<Listing, float> ListingCurYRef =
            ResolveCurY();

        // Returns the listing's current row Y. Degrades to 0f (rows draw at the top) if curY is gone.
        public static float GetListingCurY(Listing listing)
            => ListingCurYRef != null && listing != null ? ListingCurYRef(listing) : 0f;

        // ---- Widgets allow/disallow paint state ---------------------------------------------------
        private static readonly AccessTools.FieldRef<bool> CheckboxPaintingRef =
            ResolveStaticBool("checkboxPainting");
        private static readonly AccessTools.FieldRef<bool> CheckboxPaintStateRef =
            ResolveStaticBool("checkboxPaintingState");

        // True while a vanilla left-drag checkbox paint is in progress. We read checkboxPainting
        // directly (not Widgets.Painting) so a dropdown paint can't false-trigger. Degrades to false.
        public static bool WidgetsCheckboxPainting => CheckboxPaintingRef != null && CheckboxPaintingRef();

        // The value being painted (true = allow). Degrades to false.
        public static bool WidgetsCheckboxPaintingState => CheckboxPaintStateRef != null && CheckboxPaintStateRef();

        // ---- InspectTabBase.size (storage-tab frame width) ----------------------------------------
        private static readonly AccessTools.FieldRef<InspectTabBase, Vector2> TabSizeRef = ResolveTabSize();

        // Widen an inspect tab's frame to `width`, preserving its height. The frame is drawn from
        // this instance field (see InspectTabBase.TabRect), which the ctor copies from the static
        // WinSize once — too early for our startup overwrite to reach. No-op if the seam is gone.
        public static void SetTabWidth(InspectTabBase tab, float width)
        {
            if (TabSizeRef == null || tab == null) return;
            ref Vector2 s = ref TabSizeRef(tab);
            if (s.x != width) s.x = width;
        }

        private static AccessTools.FieldRef<InspectTabBase, Vector2> ResolveTabSize()
        {
            try { return AccessTools.FieldRefAccess<InspectTabBase, Vector2>("size"); }
            catch (Exception ex) { LogMissing("InspectTabBase.size", ex); return null; }
        }

        // ---- ITab_Storage private accessors -------------------------------------------------------
        private static readonly MethodInfo SelStoreSettingsParentGetter =
            ResolveGetter(typeof(ITab_Storage), "SelStoreSettingsParent");
        private static readonly FieldInfo ThingFilterStateField =
            ResolveField(typeof(ITab_Storage), "thingFilterState");
        private static readonly FieldInfo QuickSearchField =
            ThingFilterStateField != null ? ResolveField(ThingFilterStateField.FieldType, "quickSearch") : null;
        private static readonly FieldInfo QuickSearchFilterField =
            QuickSearchField != null ? ResolveField(QuickSearchField.FieldType, "filter") : null;

        // The selected storage's settings parent, or null. Best-effort: callers must null-check.
        public static IStoreSettingsParent GetSelStoreSettingsParent(ITab_Storage tab)
        {
            if (SelStoreSettingsParentGetter == null || tab == null) return null;
            // Deliberate silent swallow: this runs on a per-frame UI path, so a per-call Log would
            // spam. The resolve-time LogMissing already reports a vanished seam once at startup; here
            // callers null-check, so degrading to null is safe.
            try { return SelStoreSettingsParentGetter.Invoke(tab, null) as IStoreSettingsParent; }
            catch { return null; }
        }

        // The tab's quick-search filter, or null. Best-effort: callers must null-check.
        public static QuickSearchFilter GetQuickSearchFilter(ITab_Storage tab)
        {
            if (tab == null || ThingFilterStateField == null || QuickSearchField == null || QuickSearchFilterField == null)
                return null;
            try
            {
                object state = ThingFilterStateField.GetValue(tab);
                if (state == null) return null;
                object qs = QuickSearchField.GetValue(state);
                if (qs == null) return null;
                return QuickSearchFilterField.GetValue(qs) as QuickSearchFilter;
            }
            // Deliberate silent swallow (per-frame UI path; resolve-time LogMissing already reports a
            // vanished seam once at startup; callers null-check).
            catch { return null; }
        }

        // ---- Overload-sensitive vanilla methods ---------------------------------------------------
        // Exact signatures pinned so Harmony resolves the intended overload (no AmbiguousMatchException).
        public static readonly Type[] CheckboxSig =
        {
            typeof(float), typeof(float), typeof(bool).MakeByRefType(),
            typeof(float), typeof(bool), typeof(bool), typeof(Texture2D), typeof(Texture2D)
        };
        public static readonly Type[] CheckboxMultiSig =
        {
            typeof(Rect), typeof(MultiCheckboxState), typeof(bool)
        };

        public static MethodBase CheckboxMethod()
            => AccessTools.Method(typeof(Widgets), nameof(Widgets.Checkbox), CheckboxSig);

        public static MethodBase CheckboxMultiMethod()
            => AccessTools.Method(typeof(Widgets), nameof(Widgets.CheckboxMulti), CheckboxMultiSig);

        // ---- Pick Up And Haul (soft dependency — no compile/load-time reference) -------------------
        public const string PuahCapacityAtId = "PickUpAndHaul.WorkGiver_HaulToInventory:CapacityAt";

        // Resolved via member-id string; returns null when PUAH is absent (gates the patch via Prepare).
        public static MethodBase PuahCapacityAt()
            => AccessTools.Method(PuahCapacityAtId, new[] { typeof(Thing), typeof(IntVec3), typeof(Map) });

        // ---- resolution helpers (resolve once, log once, degrade) ---------------------------------
        private static AccessTools.FieldRef<Listing, float> ResolveCurY()
        {
            try { return AccessTools.FieldRefAccess<Listing, float>("curY"); }
            catch (Exception ex) { LogMissing("Listing.curY", ex); return null; }
        }

        private static AccessTools.FieldRef<bool> ResolveStaticBool(string fieldName)
        {
            try
            {
                var fi = AccessTools.Field(typeof(Widgets), fieldName);
                if (fi == null) { LogMissing("Widgets." + fieldName, null); return null; }
                return AccessTools.StaticFieldRefAccess<bool>(fi);
            }
            catch (Exception ex) { LogMissing("Widgets." + fieldName, ex); return null; }
        }

        private static MethodInfo ResolveGetter(Type type, string propertyName)
        {
            var mi = AccessTools.PropertyGetter(type, propertyName);
            if (mi == null) LogMissing((type?.Name ?? "?") + "." + propertyName + " (getter)", null);
            return mi;
        }

        private static FieldInfo ResolveField(Type type, string name)
        {
            var fi = AccessTools.Field(type, name);
            if (fi == null) LogMissing((type?.Name ?? "?") + "." + name, null);
            return fi;
        }

        private static void LogMissing(string member, Exception ex)
        {
            Log.Error("[PSC] Vanilla reflection seam not found: " + member
                + ". A RimWorld update may have renamed or removed it; the dependent PSC feature is "
                + "disabled until PscReflection is updated." + (ex != null ? " " + ex : ""));
        }
    }
}
