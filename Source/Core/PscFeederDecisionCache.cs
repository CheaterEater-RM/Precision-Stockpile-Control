using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;

namespace PrecisionStockpileControl
{
    // Cross-search memo for the psc.FeederAllows(source, target) functional-edge test (the dominant
    // per-candidate cost when a stack relays down a chain). Nothing else moves into the cache: the
    // invalid-in-source evacuation, carried-route, loose-item, and target onlyFromSource branches stay
    // live (store-search rewrite, Phase 3a).
    //
    // Keyed by the CANONICAL GROUP REFERENCES (source.group, dest.group), NOT UniqueLoadID strings
    // (store-search rewrite, Phase 1 S2). The old string key cost two UniqueLoadID getter calls plus a
    // string-tuple hash on EVERY call — hit or miss — purely to build the key (the single largest feeder
    // string cost in the big-map profile). A PscHaulUnit canonicalizes to one stable group object per unit
    // for a session, so reference identity is the correct key — keyed via an EXPLICIT reference-identity
    // comparer (ReferenceEquals + RuntimeHelpers.GetHashCode, RefPairComparer below, mirroring
    // PscHaulUnit.ReferenceComparer), NOT ValueTuple's default equality. The default would delegate to
    // EqualityComparer<ISlotGroup>.Default and HONOR a value-equality Equals/GetHashCode override, so a
    // modded slot group with value equality could alias two distinct units onto one cached decision; the
    // explicit comparer is reference identity regardless. BEHAVIOR-NEUTRAL: a miss computes the SAME
    // psc.FeederAllows(source, dest) — both
    // the direct-edge and the skip-hop path — so the cached value equals the live answer by construction;
    // only the key representation changed.
    //
    // Stamped with the (selectionGen, feeder-generation) pair it was built under. selectionGen carries
    // priority / order / policy / feeder-skip changes; the feeder generation carries structural edge
    // mutations. Neither counter alone is sufficient (a band edit does not bump the feeder generation, an
    // edge add does not bump selectionGen), so the cache keys on BOTH; a mismatch on either is a lazy
    // whole-cache flush. Main-thread only (PHASE4 §6.1): a plain Dictionary, no lock — the engine's haul
    // search is synchronous and main-thread, so the prior ConcurrentDictionary + double-checked flush lock
    // defended a caller that does not exist. Cleared on map removal and feeder prune so a now-dead group
    // object never lingers as a key (reference entries pin the group, unlike the old strings).
    public sealed class PscFeederDecisionCache
    {
        private readonly Dictionary<(ISlotGroup, ISlotGroup), bool> memo =
            new Dictionary<(ISlotGroup, ISlotGroup), bool>(RefPairComparer.Instance);
        private int selStamp = int.MinValue;
        private int feederStamp = int.MinValue;

        // Reference-identity comparer for the (source.group, dest.group) key, mirroring
        // PscHaulUnit.ReferenceComparer. Forces ReferenceEquals + RuntimeHelpers.GetHashCode on each
        // ISlotGroup so the key stays reference identity even if a modded slot group overrides
        // Equals/GetHashCode with value equality (which ValueTuple's default comparer would honor and could
        // alias distinct units). Directional combine: (a -> b) hashes differently from (b -> a), matching
        // the directed feeder edge. Null-safe (no null keys are inserted; the lookup early-outs first).
        private sealed class RefPairComparer : IEqualityComparer<(ISlotGroup, ISlotGroup)>
        {
            public static readonly RefPairComparer Instance = new RefPairComparer();
            public bool Equals((ISlotGroup, ISlotGroup) x, (ISlotGroup, ISlotGroup) y)
                => ReferenceEquals(x.Item1, y.Item1) && ReferenceEquals(x.Item2, y.Item2);
            public int GetHashCode((ISlotGroup, ISlotGroup) obj)
                => unchecked((RuntimeHelpers.GetHashCode(obj.Item1) * 397) ^ RuntimeHelpers.GetHashCode(obj.Item2));
        }

        public bool FeederAllows(PscMapComponent psc, PscHaulUnit source, PscHaulUnit dest)
        {
            int sel = psc.SelectionGen;
            int fdr = psc.Links.Generation;
            if (sel != selStamp || fdr != feederStamp)
            {
                memo.Clear();
                selStamp = sel;
                feederStamp = fdr;
            }

            var s = source.group;
            var d = dest.group;
            if (s == null || d == null) return psc.FeederAllows(source, dest);   // uncacheable: compute live
            var key = (s, d);
            if (memo.TryGetValue(key, out bool cached)) return cached;
            bool result = psc.FeederAllows(source, dest);
            memo[key] = result;
            return result;
        }

        public void Clear() => memo.Clear();
    }
}
