using System.Reflection;
using HarmonyLib;
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

        static PscStartup()
        {
            var harmony = new Harmony(HarmonyId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }
    }
}
