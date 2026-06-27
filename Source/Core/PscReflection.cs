using System;
using System.Collections.Generic;
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
    //   PickUpAndHaul.JobDriver_HaulToInventory:TryMakePreToilReservations(bool)  (soft dep, capture seam)
    //   PickUpAndHaul.WorkGiver_HaulToInventory:TryFindBestBetterStoreCellFor(...) (soft dep, bulk-adapter seam)
    //   PickUpAndHaul.WorkGiver_HaulToInventory:skipCells           (soft dep, private static field)
    //   HaulersDream.BulkHaul:StorageSpaceForDef(Pawn,Thing,IntVec3,Map)          (soft dep, private)
    //   HaulDestinationManager.map            (private field)   — owning map for the selection-gen chokepoint
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

        // The PUAH bulk-gather job driver's reservation seam — the COMMITTED point (reservations succeeded,
        // job about to run) where the queued items to be hauled into inventory are still spawned in their
        // source, so PSC can snapshot feeder-source provenance before SplitOff/merge destroys it. PUAH
        // overrides this method, so resolving by member id targets PUAH's own override (not base JobDriver).
        public const string PuahHaulToInventoryReserveId = "PickUpAndHaul.JobDriver_HaulToInventory:TryMakePreToilReservations";

        public static MethodBase PuahHaulToInventoryReserve()
            => AccessTools.Method(PuahHaulToInventoryReserveId, new[] { typeof(bool) });

        // PUAH's PRIVATE extra-item destination search (WorkGiver_HaulToInventory.TryFindBestBetterStoreCellFor).
        // It gates on `slotGroup.Settings.Priority <= currentPriority` (a STRICTLY-higher vanilla-band test), so it
        // is blind to PSC's same-band fine-order feeder routing and never plans a same-band chain hop during a bulk
        // gather. PSC prefixes it to delegate the choice to the engine (PickUpAndHaul_Patch), restoring bulk hauls
        // into the chain. Distinct from the vanilla StoreUtility method of the same name: this is PUAH's own static.
        public const string PuahExtraItemStoreCellId = "PickUpAndHaul.WorkGiver_HaulToInventory:TryFindBestBetterStoreCellFor";

        public static MethodBase PuahExtraItemStoreCell()
            => AccessTools.Method(PuahExtraItemStoreCellId,
                new[] { typeof(Thing), typeof(Pawn), typeof(Map), typeof(StoragePriority), typeof(Faction), typeof(IntVec3).MakeByRefType() });

        // PUAH's static `skipCells` set (cells already allocated this gather). The adapter reads it to (a) feed the
        // engine as ExcludedCells and (b) add its chosen cell back, replicating PUAH's own skip so its loop advances.
        // Null between gathers (PUAH sets it to a fresh set in JobOnThing and nulls it after); the adapter null-checks.
        private static readonly AccessTools.FieldRef<HashSet<IntVec3>> PuahSkipCellsRef = ResolvePuahSkipCells();

        public static HashSet<IntVec3> PuahSkipCells() => PuahSkipCellsRef != null ? PuahSkipCellsRef() : null;

        private static AccessTools.FieldRef<HashSet<IntVec3>> ResolvePuahSkipCells()
        {
            // PUAH absent -> TypeByName/Field returns null (the normal soft-dependency case) and the adapter no-ops;
            // only a genuine load fault reaches the catch and logs once, per AGENTS.md (no silent swallow).
            try
            {
                var fi = AccessTools.Field("PickUpAndHaul.WorkGiver_HaulToInventory:skipCells");
                if (fi == null) return null;
                return AccessTools.StaticFieldRefAccess<HashSet<IntVec3>>(fi);
            }
            catch (Exception ex) { LogMissing("PickUpAndHaul.WorkGiver_HaulToInventory.skipCells", ex); return null; }
        }

        // ---- Hauler's Dream (soft dependency — no compile/load-time reference) ----------------------
        // BulkHaul.StorageSpaceForDef is HD's per-destination capacity probe (the analogue of PUAH's
        // CapacityAt). It is PRIVATE STATIC and HD-internal, so it is the most version-fragile seam here;
        // AccessTools resolves non-public members, and a future HD rename makes Prepare() return null so
        // the patch silently degrades to admission + carry-drop-cap coverage (HD's destination selection
        // already routes through AllowedToAccept).
        public const string HaulersDreamStorageSpaceForDefId = "HaulersDream.BulkHaul:StorageSpaceForDef";

        public static MethodBase HaulersDreamStorageSpaceForDef()
            => AccessTools.Method(HaulersDreamStorageSpaceForDefId, new[] { typeof(Pawn), typeof(Thing), typeof(IntVec3), typeof(Map) });

        // ---- LWM Deep Storage detection (soft dependency — no compile/load-time reference) ----------
        // The CompDeepStorage type, resolved once (null when LWM is absent). The store-search engine uses it
        // to DECLINE takeover for any item currently resting in a Deep Storage cell: a deliberate Phase 2
        // broad stance (PSC cedes ALL DSU-resident items to LWM's own relocation transpiler, not only
        // over-capacity ones, rather than reproducing LWM's per-item / per-cell weight + stack capacity model).
        // Fail-safe: a resolution miss or absent LWM yields null here, so IsItemInDeepStorage returns false and
        // PSC behaves as if no Deep Storage is present. A finer "over-capacity only" probe is a later refinement.
        private static readonly Type DeepStorageCompType = ResolveTypeByName("LWM.DeepStorage.CompDeepStorage");

        // True when the item is RESTING in a storage BUILDING carrying a CompDeepStorage (a DSU). Resolve the
        // item's OWN slot group (GetSlotGroup uses Thing.Position, null when unspawned) rather than probing the
        // holder's cell: a carried/in-container item must NOT inherit whatever storage sits under the hauler, or
        // a pawn standing on a DSU would wrongly cede the search for an item that is not in Deep Storage at all.
        // Plain stockpile zones (parent is a Zone, not a ThingWithComps) and a missing LWM both read false.
        public static bool IsItemInDeepStorage(Thing t)
        {
            if (DeepStorageCompType == null) return false;        // LWM absent
            if (t == null || !t.Spawned) return false;            // carried / in a container: not DSU-resident
            var slot = t.GetSlotGroup();
            if (slot?.parent is ThingWithComps building)
            {
                var comps = building.AllComps;
                for (int i = 0; i < comps.Count; i++)
                    if (DeepStorageCompType.IsInstanceOfType(comps[i])) return true;
            }
            return false;
        }

        private static Type ResolveTypeByName(string name)
        {
            // TypeByName returns null when the type is simply absent (the normal soft-dependency case, e.g. LWM
            // not installed) -- that path stays silent and DSU detection no-ops. Only a genuine throw (an
            // assembly load fault) reaches the catch; per AGENTS.md it must not be swallowed silently, so log
            // once and degrade to null.
            try { return AccessTools.TypeByName(name); }
            catch (Exception ex)
            {
                Log.Error("[PSC] Error resolving optional type '" + name + "' (soft dependency); the dependent "
                    + "feature is disabled. " + ex);
                return null;
            }
        }

        // ---- HaulDestinationManager.map (owning map for the selection-gen chokepoint) -----------------
        private static readonly AccessTools.FieldRef<HaulDestinationManager, Map> HaulDestMapRef = ResolveHaulDestMap();

        // The map owning a HaulDestinationManager, read by the priority-change chokepoint (SelectionGen_Patches)
        // to bump that map's selectionGen. Degrades to null if the field is gone; the caller then
        // over-invalidates all maps (safe), so a vanished seam never serves a stale feeder-decision cache.
        public static Map GetHaulDestinationMap(HaulDestinationManager mgr)
            => HaulDestMapRef != null && mgr != null ? HaulDestMapRef(mgr) : null;

        private static AccessTools.FieldRef<HaulDestinationManager, Map> ResolveHaulDestMap()
        {
            try { return AccessTools.FieldRefAccess<HaulDestinationManager, Map>("map"); }
            catch (Exception ex) { LogMissing("HaulDestinationManager.map", ex); return null; }
        }

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
