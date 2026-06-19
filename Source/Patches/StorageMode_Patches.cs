using HarmonyLib;
using RimWorld;
using Verse;

namespace PrecisionStockpileControl
{
    // M5.2 Flickable-style "freeze". Items inside an Off / Accept-only storage read as forbidden to
    // the player faction WITHOUT ever writing CompForbiddable.Forbidden. This is a pure read-side
    // answer ("not usable right now"): it never fights the player's manual forbid, never persists,
    // and can't strand a forbidden item when the pile is deleted or the mod is removed (the flag is
    // never touched — see docs/04_PSC_DESIGN.md and the plan's deletion-safety note).
    //
    // Seam: the (Thing, Faction) overload is the leaf every player-side "can I use this?" check
    // routes through — IsForbidden(Thing, Pawn) calls t.IsForbidden(pawn.Faction), and only this
    // overload actually reads compForbiddable.Forbidden (RimWorld/ForbidUtility.cs). One postfix
    // therefore covers cooks, doctors, refuelers, builders and haulers (a forbidden item is
    // automatically non-haulable via ListerHaulables.ShouldBeHaulable), which is why modes need no
    // separate haul-out patch. Explicit Type[] avoids the AmbiguousMatchException hard rule.
    //
    // TIGHTEN-ONLY: only ever turns a not-forbidden item forbidden; never the reverse.
    [HarmonyPatch(typeof(ForbidUtility), nameof(ForbidUtility.IsForbidden), new[] { typeof(Thing), typeof(Faction) })]
    public static class ForbidUtility_IsForbidden_ModePatch
    {
        public static void Postfix(Thing t, Faction faction, ref bool __result)
        {
            if (__result) return;                        // already forbidden — never un-forbid
            if (PscStorageDataStore.IsEmpty) return;     // cheapest early-out
            if (faction != Faction.OfPlayer) return;     // vanilla only forbids for the player faction
            if (t == null || !t.Spawned) return;

            var psc = PscMapComponent.For(t.Map);
            if (psc == null || !psc.anyFreezeModeActive) return;  // per-map gate

            var unit = PscHaulUnit.ResolveCurrent(t);    // the item's current slot-group unit
            if (!unit.IsValid) return;

            var data = PscStorageDataStore.TryGet(unit.Settings);
            if (data != null && (data.mode == PscStorageMode.Off || data.mode == PscStorageMode.AcceptOnly))
                __result = true;                         // virtual freeze — real flag untouched
        }
    }
}
