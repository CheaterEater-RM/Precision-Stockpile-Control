using System.Collections.Generic;
using Verse;

namespace PrecisionStockpileControl
{
    // A limit group: several ThingDefs in one stockpile sharing ONE limit that governs their COMBINED
    // item total (e.g. all meats kept between 100 and 125 TOTAL). The shared limit lives here exactly
    // once, so editing any member edits the whole group. Limits are raw item totals — NO per-def stack
    // conversion (a group spans defs with different stackLimits, so a stack count would be meaningless).
    //
    // Per-unit; rides PscStorageData's <psc> node (write-nothing-when-empty). Removal-safe: members are
    // persisted as defNames (LookMode.Value) so a removed CONTENT mod degrades quietly — the runtime
    // `members` list is re-resolved from the names on load (PscStorageData self-heals groups that drop
    // below the 2-member minimum). The shared limit's PscRefillState rides this group's Deep round-trip,
    // so a deliberately-drained Satisfied group reloads Satisfied (reconciled, not re-seeded).
    public class PscLimitGroup : IExposable
    {
        // The shared lower/upper/refill (raw item totals). Editing any member edits this one object.
        public PscDefLimit limit = new PscDefLimit();

        // Persisted member defNames. Strings (not LookMode.Def) so a missing content def loads silently;
        // the list overload of Scribe_Collections.Look has no logNullErrors knob, unlike the dict.
        public List<string> memberNames = new List<string>();

        // Auto-assigned uppercase id, unique within the unit (A..Z, then AA, AB, …). Display + tooltip.
        public string letter;

        // Optional custom name (null/empty = none). Shown only in the tooltip and the group editor —
        // never on the cramped filter row, which shows just the letter.
        public string name;

        // Runtime-only: member defs resolved from memberNames. Never scribed; rebuilt by ResolveMembers
        // on load and by PscStorageData.RebuildGroupIndex after edits. Always non-null.
        public List<ThingDef> members = new List<ThingDef>();

        public bool IsDefault => limit == null || limit.IsDefault;

        // Resolve runtime `members` from persisted `memberNames`, dropping defs that no longer exist
        // (content mod removed). GetNamedSilentFail keeps the documented "silent self-heal on load".
        public void ResolveMembers()
        {
            members.Clear();
            if (memberNames == null) { memberNames = new List<string>(); return; }
            for (int i = 0; i < memberNames.Count; i++)
            {
                var n = memberNames[i];
                if (string.IsNullOrEmpty(n)) continue;
                var d = DefDatabase<ThingDef>.GetNamedSilentFail(n);
                if (d != null) members.Add(d);
            }
        }

        // Write persisted `memberNames` from runtime `members` (after an in-memory membership edit), so
        // persistence and the resolved set stay aligned.
        public void SyncNames()
        {
            memberNames.Clear();
            for (int i = 0; i < members.Count; i++)
                if (members[i] != null) memberNames.Add(members[i].defName);
        }

        // Deep copy for paste. PscDefLimit.Clone resets refill to Unset (a paste is fresh policy, not a
        // restore); the caller re-seeds via Notify_GroupLimitSet.
        public PscLimitGroup Clone()
        {
            var g = new PscLimitGroup
            {
                limit = limit != null ? limit.Clone() : new PscDefLimit(),
                letter = letter,
                name = name,
                memberNames = new List<string>(memberNames ?? new List<string>()),
            };
            g.ResolveMembers();
            return g;
        }

        public void ExposeData()
        {
            Scribe_Deep.Look(ref limit, "limit");
            Scribe_Collections.Look(ref memberNames, "members", LookMode.Value);
            Scribe_Values.Look(ref letter, "letter");
            Scribe_Values.Look(ref name, "name");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (limit == null) limit = new PscDefLimit();
                if (memberNames == null) memberNames = new List<string>();
            }
        }
    }
}
