using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Harmony bootstrap. Kept in its own [StaticConstructorOnStartup] class, separate from the
    // Mod subclass (PscMod) — mixing the two causes silent init failures because the Mod ctor
    // fires before Defs load while static constructors fire after ResolveReferences.
    [StaticConstructorOnStartup]
    public static class PscStartup
    {
        public const string HarmonyId = "com.cheatereater.precisionstockpilecontrol";

        // The vanilla storage tab width PSC widens to. The vanilla default is 300; the extra room
        // lets the per-row limit labels sit beside long item names without smearing them.
        public const float StorageTabWidth = 360f;

        // True only when the static WinSize overwrite below succeeded. The TabRect-getter patch
        // (InspectTabBase_TabRect_Patch) gates on this so we never widen the window FRAME while the
        // FillTab CONTENT is still drawing at the vanilla 300 — i.e. the two width sources move
        // together or not at all.
        public static bool StorageTabWidened { get; private set; }

        static PscStartup()
        {
            var harmony = new Harmony(HarmonyId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            WidenStorageTab();
        }

        // The storage tab has TWO width sources. WinSize (static readonly Vector2) drives only the
        // FillTab CONTENT rect (new Rect(0,0,WinSize.x,WinSize.y)), read live each draw — so this
        // overwrite is enough to widen the content. The window FRAME instead comes from the per-tab
        // instance field InspectTabBase.size, which ITab_Storage's ctor copies from WinSize *once*.
        // The shared ITab_Storage instance is built during ThingDef.ResolveReferences (before this
        // [StaticConstructorOnStartup] runs), so it already captured size=300 and this overwrite
        // can't reach it — the frame is corrected separately by InspectTabBase_TabRect_Patch.
        //
        // Cosmetic only — does not touch any storage behaviour. Fail-safe: on reflection failure,
        // log, leave the vanilla width, and leave StorageTabWidened false so the frame stays 300
        // too (the label still hugs its text, so it degrades to plain vanilla geometry).
        private static void WidenStorageTab()
        {
            try
            {
                var f = AccessTools.Field(typeof(ITab_Storage), "WinSize");
                if (f == null)
                {
                    Log.Error("[PSC] Could not widen ITab_Storage.WinSize: field not found "
                              + "(RimWorld version may have changed).");
                    return;
                }
                var cur = (Vector2)f.GetValue(null);
                f.SetValue(null, new Vector2(StorageTabWidth, cur.y));
                StorageTabWidened = true;
            }
            catch (Exception e)
            {
                Log.Error("[PSC] Could not widen ITab_Storage.WinSize "
                          + "(RimWorld version may have changed): " + e);
            }
        }
    }
}
