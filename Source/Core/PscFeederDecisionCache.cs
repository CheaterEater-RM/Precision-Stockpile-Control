using System.Collections.Concurrent;

namespace PrecisionStockpileControl
{
    // Cross-search memo for ONE subquery inside the still-live PscAdmissionIndex.TryFeederReject: the
    // psc.FeederAllows(source, target) functional-edge membership test (the dominant per-candidate cost when a
    // stack relays down a chain). Nothing else moves into the cache: the invalid-in-source evacuation,
    // carried-route, loose-item, and target onlyFromSource branches stay live (store-search rewrite, Phase 3a).
    //
    // Keyed by (sourceId, destId) strings, stamped with the (selectionGen, feeder-generation) pair it was built
    // under. selectionGen carries priority / order / policy / feeder-skip changes; the feeder generation carries
    // structural edge mutations. Neither counter alone is sufficient (a band edit does not bump the feeder
    // generation, an edge add does not bump selectionGen), so the cache keys on BOTH. A mismatch on either is a
    // lazy whole-cache flush. Concurrent-safe because store searches run on off-main reachability threads
    // (mirrors PscHaulUnit.idCache's model): a ConcurrentDictionary body, a double-checked lock only around the
    // rare flush.
    public sealed class PscFeederDecisionCache
    {
        private readonly ConcurrentDictionary<(string, string), bool> memo =
            new ConcurrentDictionary<(string, string), bool>();
        private readonly object flushLock = new object();
        private int selStamp = int.MinValue;
        private int feederStamp = int.MinValue;

        public bool FeederAllows(PscMapComponent psc, PscHaulUnit source, PscHaulUnit dest)
        {
            int sel = psc.SelectionGen;
            int fdr = psc.Links.Generation;
            if (sel != selStamp || fdr != feederStamp)
            {
                lock (flushLock)
                {
                    if (sel != selStamp || fdr != feederStamp)
                    {
                        memo.Clear();
                        selStamp = sel;
                        feederStamp = fdr;
                    }
                }
            }

            string sId = source.UniqueLoadID, dId = dest.UniqueLoadID;
            if (sId == null || dId == null) return psc.FeederAllows(source, dest);   // uncacheable: compute live
            var key = (sId, dId);
            if (memo.TryGetValue(key, out bool cached)) return cached;
            bool result = psc.FeederAllows(source, dest);
            memo[key] = result;
            return result;
        }

        public void Clear() => memo.Clear();
    }
}
