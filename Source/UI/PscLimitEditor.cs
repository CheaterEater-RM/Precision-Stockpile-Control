using RimWorld;
using UnityEngine;
using Verse;

namespace PrecisionStockpileControl
{
    // Shared working-state widget for editing one (lower?, upper?) limit pair. Used by the global
    // control window (apply-to-search) and the per-item / category submenu, so the controls behave
    // identically everywhere. Limits are tracked in items; "stacks" mode multiplies by each def's
    // live stackLimit at apply time (PSC never caches stack limits — D13).
    public class PscLimitEditor
    {
        public bool stacksMode;
        public bool upperUnlimited = true;
        public int upperVal = 10;
        public string upperBuf = "10";
        public bool lowerAlways = true;
        public int lowerVal;
        public string lowerBuf = "0";

        public void Draw(Listing_Standard list)
        {
            if (list.ButtonTextLabeled("PSC_Mode".Translate(),
                    (stacksMode ? "PSC_ModeStacks" : "PSC_ModeItems").Translate()))
            {
                stacksMode = !stacksMode;
            }
            float fieldMax = stacksMode ? 99f : 5000f;

            list.Gap(4f);
            list.CheckboxLabeled("PSC_UpperUnlimited".Translate(), ref upperUnlimited);
            if (!upperUnlimited)
            {
                list.TextFieldNumericLabeled("PSC_Upper".Translate(), ref upperVal, ref upperBuf, 1, fieldMax);
                upperVal = Mathf.RoundToInt(list.Slider(upperVal, 1, fieldMax));
                upperBuf = upperVal.ToString();
            }

            list.Gap(4f);
            list.CheckboxLabeled("PSC_LowerAlways".Translate(), ref lowerAlways);
            if (!lowerAlways)
            {
                float lowerMax = upperUnlimited ? fieldMax : upperVal;
                list.TextFieldNumericLabeled("PSC_Lower".Translate(), ref lowerVal, ref lowerBuf, 0, lowerMax);
                lowerVal = Mathf.RoundToInt(list.Slider(lowerVal, 0, lowerMax));
                lowerBuf = lowerVal.ToString();
            }
        }

        public string PreviewString()
        {
            string lo = lowerAlways ? "PSC_Always".Translate().ToString() : lowerVal.ToString();
            string hi = upperUnlimited ? "∞" : upperVal.ToString();
            string units = (stacksMode ? "PSC_ModeStacks" : "PSC_ModeItems").Translate();
            return lo + " — " + hi + " (" + units + ")";
        }

        // Build a limit for a specific def (applies the stacks multiplier on the def's live stackLimit).
        public PscDefLimit ToLimit(ThingDef def)
        {
            int mult = stacksMode ? Mathf.Max(1, def.stackLimit) : 1;
            int? upper = upperUnlimited ? (int?)null : Mathf.Max(1, upperVal) * mult;
            int? lower = lowerAlways ? (int?)null : Mathf.Max(0, lowerVal) * mult;
            if (upper.HasValue && lower.HasValue && lower.Value > upper.Value) lower = upper;
            return new PscDefLimit { Upper = upper, Lower = lower };
        }

        // Seed working state (items mode) from an existing limit, for the per-def submenu.
        public void LoadFrom(PscDefLimit lim)
        {
            stacksMode = false;
            upperUnlimited = lim == null || !lim.Upper.HasValue;
            upperVal = (lim != null && lim.Upper.HasValue) ? lim.Upper.Value : 10;
            upperBuf = upperVal.ToString();
            lowerAlways = lim == null || !lim.Lower.HasValue;
            lowerVal = (lim != null && lim.Lower.HasValue) ? lim.Lower.Value : 0;
            lowerBuf = lowerVal.ToString();
        }
    }

    // Single place that mutates a unit's policy for one def + keeps the vanilla filter in sync.
    // Callers batch several defs then call PscMapComponent.NotifyPolicyChanged once.
    internal static class PscEdit
    {
        public static void ApplyLimit(StorageSettings settings, PscHaulUnit unit, ThingDef def, PscDefLimit lim)
        {
            var data = PscStorageDataStore.GetOrCreate(settings);
            if (lim == null || lim.IsDefault) data.limits.Remove(def);
            else data.limits[def] = lim;
            settings.filter.SetAllow(def, true);
            data.Notify_LimitSet(def, unit);
        }

        public static void ClearLimit(StorageSettings settings, ThingDef def, bool allow)
        {
            PscStorageDataStore.TryGet(settings)?.limits.Remove(def);
            settings.filter.SetAllow(def, allow);
        }
    }
}
