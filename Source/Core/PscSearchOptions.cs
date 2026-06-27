using System.Collections.Generic;
using Verse;

namespace PrecisionStockpileControl
{
    // The store-search engine's call contract (store-search rewrite, Phase 2). The engine is exposed as a
    // callable API (not only a Harmony prefix) so the Phase 4 bulk adapters reuse one selection truth; this
    // struct is how a caller parameterises a search.
    //
    //   - ExcludedCells: cells the engine must treat as ineligible. The Phase 4 PUAH extra-item adapter passes
    //     PUAH's skipCells here; null / empty on the ordinary vanilla-prefix path, which pays nothing.
    //   - NeedAccurateResult: forwarded to the delegated vanilla TryFindBestBetterStoreCellForIn.
    //   - Caller: which entry routed here (vanilla prefix vs a bulk adapter). DELIBERATELY UNWIRED: set but
    //     not read yet; retained for Phase 4 bulk-adapter behaviour + logging.
    public readonly struct PscSearchOptions
    {
        public readonly HashSet<IntVec3> ExcludedCells;
        public readonly bool NeedAccurateResult;
        public readonly PscSearchCaller Caller;

        public PscSearchOptions(HashSet<IntVec3> excludedCells, bool needAccurateResult, PscSearchCaller caller)
        {
            ExcludedCells = excludedCells;
            NeedAccurateResult = needAccurateResult;
            Caller = caller;
        }

        // The ordinary vanilla-prefix search: no excluded cells, default caller.
        public static PscSearchOptions Default(bool needAccurateResult)
            => new PscSearchOptions(null, needAccurateResult, PscSearchCaller.VanillaPrefix);
    }

    // HD needs no adapter value: it routes through vanilla store-search (which PSC's engine already owns), unlike
    // PUAH's own private picker. So there is no HdAdapter caller -- only the vanilla prefix and the PUAH adapter.
    public enum PscSearchCaller { VanillaPrefix, PuahExtraItem }
}
