# PSC Red-Team Review

*RimWorld 1.6.4850 source checked against local decompiled source. Reference-mod review covered the `Stockpile Manipulation` group, especially Variety Matters Stockpile, Stockpile Limit, Stack Gap, Storage Sorting, Satisfied Storage, and LWM Deep Storage.*

This is a hostile review of `RESEARCH.md`, with performance treated as the load-bearing requirement. The short version: the research is directionally good for **M1 core limits**, but it overstates how far a postfix-only architecture can go. The two most serious problems are:

1. **Fine-order/subpriority cannot work as described with only postfixes**, because vanilla refuses to search same-`StoragePriority` destinations when an item is already in storage of that same vanilla band.
2. **Feeder rules do not belong as naive global `AllowedToAccept` rules**, because `AllowedToAccept` is used both for candidate destinations and for asking whether an item is valid in its current storage. Without context guards, feeder settings can make a source stockpile treat its own contents as invalid.

The safest path is to keep the M1 count-cache/admission core, but tighten the invariants, defer fine-order until the search semantics are redesigned, and treat merge/direct-drop/stack-change patches as part of correctness rather than optional polish.

---

## What the research gets right

- **`StorageSettings.ExposeData` is the right persistence seam** for per-storage policy. Reference mods repeatedly use this pattern, and it is removal-safe enough for RimWorld saves because unknown XML can be ignored on load.
- **Early opt-out is mandatory.** A stockpile with no PSC data must pay only a dictionary lookup or less. This is the correct performance posture.
- **Whole-stockpile counts need a cache.** Scanning cells during `AllowedToAccept` or `NoStorageBlockersIn` would be disastrous on hauling paths.
- **`AllowedToAccept(Thing)` is a good first stockpile-level gate**, because `TryFindBestBetterStoreCellForWorker` calls it once per candidate group before scanning cells.
- **`NoStorageBlockersIn` should stay tighten-only.** Reference mods that replace it fully are much higher compatibility risk.
- **Reading stack limits live is correct.** PSC should never cache vanilla or modded stack limits as durable truth.

---

## Critical challenge 1: fine-order cannot be postfix-only

`RESEARCH.md` says fine-order can be implemented with:

- a postfix on `HaulDestinationManager.CompareSlotGroupPrioritiesDescending`, and
- a postfix on `StoreUtility.TryFindBestBetterStoreCellFor`.

That is not sufficient.

Vanilla `TryFindBestBetterStoreCellFor` starts with `foundPriority = currentPriority` and breaks when a candidate group's vanilla priority is `<= currentPriority`. If an item is currently in a Preferred stockpile, vanilla will not consider other Preferred stockpiles at all. Therefore a `5b -> 5a` move cannot be discovered by a postfix after the method returns, because vanilla will usually return `false`.

### Consequence

Subpriority and 1-10 half-steps cannot create real within-band movement under the proposed seam. They can only re-rank candidate groups when vanilla was already willing to search that band, which excludes the main use case: moving from lower fine-order to higher fine-order inside the same vanilla band.

### Better approach

Use a prefix/postfix pair on `TryFindBestBetterStoreCellFor`:

- In the prefix, detect the source haul unit and its PSC fine-order.
- If fine-order is active and the item is already in storage, temporarily lower `currentPriority` by one vanilla band so vanilla will search the current band too.
- In the postfix, discard any candidate that does not outrank the source by full PSC order `(vanilla band, sub-tier, letter)`, then choose the best group/cell.
- For `Low`, lowering to `Unstored` is required.

This is still mostly non-invasive, but it is **not postfix-only**. It also needs careful handling for loose items, forced hauling, and invalid current storage.

### Performance warning

The proposed re-selection scans groups and cells again. Vanilla deliberately samples only a small early slice of each group in accurate mode. PSC should not turn a same-band search into a full scan of every shelf cell.

Recommended limit:

- Iterate fine-ordered groups only until the first outranking fine tier with a valid cell is found.
- Use vanilla's same sampling budget, or a small fixed cap, unless the user explicitly requests exact placement.
- Cache per-map `anyFineOrderActive`.
- Call `Notify_HaulDestinationChangedPriority` whenever fine-order changes, or the sorted group list will be stale.

---

## Critical challenge 2: feeder checks need context

The proposed admission pseudocode treats feeder rules as simple checks inside `StorageSettings.AllowedToAccept(Thing)`.

That method is used in multiple contexts:

- candidate destination evaluation,
- `Zone_Stockpile.Accepts`,
- `Building_Storage.Accepts`,
- `StoreUtility.CurrentStoragePriorityOf`,
- `ListerHaulables.ShouldBeHaulable`,
- `SlotGroup.RemoveHaulDesignationOnStoredThings`,
- drop-time checks in `Toils_Haul.PlaceHauledThingInCell`.

This matters because `onlyToDestinations` is a rule about **outbound hauling from this storage to another storage**, not about whether the source storage accepts its own contents.

### Failure mode

If a stockpile has `onlyToDestinations` enabled and `AllowedToAccept` says "source has only-to-destinations and the candidate unit is not one of those destinations", then asking whether the source accepts its own current item can return false. That makes stored items look invalid in their own source stockpile.

That may create extra haulability churn, incorrect "best storage" answers, and repeated failed scans when no linked destination currently accepts the item.

### Required guard

`AllowedToAccept` needs to resolve both:

- the candidate target unit represented by `settings.owner`, and
- the current source unit of `t`, if `t.Spawned`.

Then feeder rules should only apply when the candidate target is different from the current source. A source must still accept its own contents for validity checks.

In pseudocode:

```csharp
source = ResolveCurrentHaulUnit(t);      // null for loose/unspawned/carried
target = ResolveSettingsHaulUnit(settings);

if (source == target)
    skip feeder movement gates;
else
    apply onlyFromSource / onlyToDestinations gates;
```

### Drop-time warning

When `Toils_Haul.PlaceHauledThingInCell` calls `slotGroup.Settings.AllowedToAccept(actor.carryTracker.CarriedThing)`, the thing is carried and unspawned, so `StoreUtility.GetSlotGroup(t)` returns null. If feeder logic rejects unspawned things, the drop is not necessarily blocked; vanilla still attempts `TryDropCarriedThing`. So `AllowedToAccept` is mainly a planning gate, not a reliable final enforcement gate.

For strict feeder semantics, disable or patch opportunistic duplicate hauling for feeder jobs, because duplicates collected en route may come from unlinked sources.

---

## Critical challenge 3: cache correctness must be an invariant

The research correctly identifies that `Notify_ReceivedThing` / `Notify_LostThing` miss in-place `stackCount` changes. But treating periodic resync as "baseline or patch SplitOff/absorption" is too soft.

PSC limits depend on counts. If counts are wrong, pawns make bad jobs. This is not just UI drift.

### Known drift sources

- `Thing.SplitOff`: reduces `stackCount` without despawning when partial.
- `Thing.TryAbsorbStack`: increases one stack and reduces/destroys another.
- `ThingUtility.TryAbsorbStackNumToTake`: used by absorption capacity logic.
- Direct placement through `GenPlace.TryPlaceDirect`.
- Pawn drop logic through `Pawn_CarryTracker.TryDropCarriedThing`.
- Bill products and carry remainders.
- Storage group link/unlink, because existing contents are not "received" again.
- Zone cell add/remove, because existing things in the added/removed cells need recounting.

Reference mods patch these because they had to. Variety Matters patches absorption. LWM dirties cache on `SplitOff`. Stack Gap patches direct placement and carry/drop paths for hard correctness.

### Recommendation

Make this the M1 invariant:

> For every PSC-tracked storage unit, cached count equals actual spawned storable item count after each cache update/resync boundary.

Implementation strategy:

- Patch `Thing.SplitOff` for spawned items and dirty the current unit/def.
- Patch `Thing.TryAbsorbStack` or `ThingUtility.TryAbsorbStackNumToTake` to dirty affected units/defs.
- Patch `Zone_Stockpile.AddCell` / `RemoveCell` to add/subtract actual contents of the cell.
- Patch `StorageGroupUtility.SetStorageGroup` or otherwise rebuild counts for affected old/new units after link/unlink.
- Keep staggered resync as a self-healing backstop, not the primary mechanism.

The periodic resync interval should be adaptive: resync only tracked units, only dirty defs where possible, and spread work across ticks.

---

## Critical challenge 4: linked storage is trickier than "settings by reference"

`StorageGroup` owns a shared `StorageSettings`, so keying PSC policy by `StorageSettings` gives shared linked settings. That part is good.

But `StorageGroup` is `IStoreSettingsParent` and `ISlotGroup`; it is **not** an `ISlotGroupParent`. A helper like `settings.owner.GetSlotGroup()` will not work generically.

### Required helper

PSC needs one canonical resolver:

```csharp
PscHaulUnit ResolveSettingsHaulUnit(StorageSettings settings)
```

It must handle:

- `StorageGroup` owner -> the storage group itself,
- `ISlotGroupParent` owner -> its slot group, or its `StorageGroup` if linked,
- null/fixed parent settings -> no PSC unit,
- non-slot haul destinations -> probably out of scope for M1.

Do not scatter this logic across patches.

### StorageGroup.CellsList warning

`StorageGroup.CellsList` returns a static temporary list. Do not retain it, store it, or call another `StorageGroup.CellsList` while iterating a previous returned list. Copy only when absolutely necessary. Prefer immediate iteration or `HeldThings` for resync.

---

## Critical challenge 5: lower-limit semantics are ambiguous

`DESIGN_GOALS.md` says lower limit `0` is default and "ensures it is always refilled." The research pseudocode says:

```csharp
if not refilling[t.def]: result = false;
```

with refilling flipping on at `count <= lower` and off at `count >= upper`.

If `lower = 0`, that means a stockpile with 1 item and upper 20 will not refill until it hits 0. That is not "always refilled"; it is maximum hysteresis.

### Recommendation

Represent lower/hysteresis as nullable or explicitly enabled:

- No lower threshold set: accept until upper.
- Lower threshold set: accept only after count falls to `lower`, then continue until upper.
- Lower threshold `0`: refill only when empty, if the player explicitly set it.

This distinction matters for both UI truthfulness and cache behavior.

---

## Critical challenge 6: batch filling is under-specified

The proposed cheap batch gate is:

```csharp
room >= batch && t.stackCount >= batch
```

This is not enough when no upper limit exists, or when physical storage capacity is below the batch amount. Vanilla `HaulToCellStorageJob` computes actual count after scanning cells. A job can be admitted and then reduced below the batch threshold.

### Better M1/M2 behavior

- Use `AllowedToAccept` for the cheap source-stack gate.
- In `HaulToCellStorageJob` postfix, cancel the job or set count to zero/null if final `job.count < batch`.
- Disable opportunistic duplicates for batch-limited jobs unless we explicitly support aggregated batch counting.

If the design goal is "never bring less than 10", the final job count is the authoritative place to enforce it.

---

## Critical challenge 7: direct drop and merge behavior define how "hard" caps are

The research accepts over-limit direct placement and natural drain. That is compatible with D2, but the player-facing language must be honest: PSC upper limits are **haul-planning limits**, not hard physical caps.

Without direct-drop/merge patches:

- bill products can land over cap,
- manual drops can land over cap,
- carried things can merge into an over-cap stack,
- opportunistic duplicates can overshoot,
- stack absorption can bypass per-stack presentation rules,
- merge jobs may undo visual expectations.

### Recommendation

Choose one contract explicitly:

1. **Soft cap contract:** PSC controls automatic storage selection and job counts only. Manual/bill/direct placement may exceed limits, then hauling will stop and excess drains only if a better target exists.
2. **Hard cap contract:** PSC must patch direct placement, stack absorption, mergeability, and possibly recipe product storage.

For performance and compatibility, I recommend **soft cap for M1**, but the UI should call it "target maximum" or similar. Do not imply an absolute cap until the hard-cap patches exist.

---

## Alternative architecture: demand-indexed storage

The current design asks every candidate storage, "can you accept this thing?" That is vanilla-shaped and compatible, but PSC can do better for limited stockpiles.

Maintain a per-map demand index:

```text
ThingDef -> list of PSC units currently refilling/accepting that def
ThingDef -> bool anyPscDemand
PscUnit -> counts, limits, current refilling state
```

When counts cross thresholds, update the index and recalc nearby/source haulables. Then the hot path can early-out faster:

- If no PSC unit has demand for `t.def`, skip PSC-specific work.
- If a target unit has no rule for `t.def`, use vanilla.
- If a target unit has a rule but is not currently demanding that def, reject quickly.

This also gives feeder links a cleaner model: destination demand can be filtered by source unit before the expensive cell search.

This is not a replacement for `AllowedToAccept`; it is a backing index that makes `AllowedToAccept` cheaper and gives `MapComponentTick` something precise to maintain.

---

## Revised milestone advice

### M1: core soft limits only

Keep this tight:

- `StorageSettings` attached data.
- Deep-copy clipboard support.
- Count cache with dirty/resync backstop.
- Explicit patches for `SplitOff`, absorption dirtying, zone cell add/remove, storage group link/unlink.
- `AllowedToAccept` upper/lower gate with clear lower semantics.
- `HaulToCellStorageJob` final clamp/cancel.
- Minimal UI that truthfully says soft target maximum.

Do **not** include feeder links, fine-order, 1-10 priorities, or hard-cap direct placement in M1.

### M2: batch and integration hardening

- Batch enforced against final job count.
- PUAH capacity patch.
- Multi-stack/Ogre/LWM test cases.
- Merge/direct-placement decision: remain soft, or start hard-cap patch set.

### M3: feeder links

- Implement with source/target context guards.
- Disable or control opportunistic duplicates.
- Add clear diagnostics for non-functional links.
- Add tests for source accepting its own contents.

### M4: fine-order/subpriority

- Redesign around prefix-adjusted `currentPriority` plus postfix selection.
- Profile before enabling by default.
- Add a debug overlay or dev log for why a same-band move was or was not selected.

### M5: migration and optional behaviors

- Other limit mod migration.
- Flickable behavior.
- Hard ejection if still wanted.

---

## Test scenarios PSC should require before implementation is considered safe

- Add PSC to an existing save with 100 vanilla stockpiles: no PSC data created, no measurable hauling slowdown.
- One shelf with upper 20, lower unset: accepts until 20.
- One shelf with upper 20, lower 5: stops at 20, restarts at 5.
- Partial consumption via `SplitOff` causes refill state to update without waiting for a full resync.
- Stack absorption changes cached counts correctly.
- Link two shelves with existing contents: group count is exact immediately after linking.
- Unlink a shelf with existing contents: old group and new member counts are exact.
- Feeder source with `onlyToDestinations` still considers its own contents valid.
- Feeder destination with `onlyFromSource` rejects loose items but accepts linked-source items at planning time.
- Feeder hauling with opportunistic duplicates cannot pick up unlinked-source extras.
- Batch 10 never creates a final job count below 10.
- Fine-order `Preferred b -> Preferred a` works; `Preferred a -> Preferred b` does not.
- Low-band fine-order works by treating `Unstored` as the lowered comparison floor.
- LWM/Ogre multi-stack cells report physical capacity correctly.
- Manual direct drop over target maximum is either blocked or documented as allowed.

---

## Final red-team verdict

The proposal is a good base, but it is too optimistic in three places:

- **Postfix-only fine-order is not viable.**
- **Feeder rules need contextual admission, not generic acceptance.**
- **Count cache drift is not a minor risk; it is the central correctness risk.**

The better architecture is still conservative: keep vanilla's hauling shape, attach PSC state to `StorageSettings`, use cached counts, and tighten only. But PSC should draw a firm line between soft planning limits and hard physical caps, and it should defer the glamorous features until the cache and search semantics are boringly correct.

---

## Addendum: where transpilers may be worth it

The original research tries hard to avoid transpilers. That is a good default, but it should not become a rule of pride. A small, well-targeted transpiler can be safer and faster than a broad postfix that redoes vanilla work.

The decision rule I would use:

> Use a transpiler only when it changes one local vanilla decision inside an otherwise valuable algorithm, and the postfix alternative either cannot express the behavior or must duplicate an expensive search.

### Field evidence from older storage mods

Transpiler risk should not be judged in the abstract. Some of the scariest-looking seams are also the seams that older, widely used storage mods have already battle-tested. That matters.

Reference-mod evidence:

- **LWM Deep Storage** transpiles `HaulAIUtility.HaulToCellStorageJob` to replace vanilla's `GetItemStackSpaceLeftFor(cell, map, thing.def)` call with a thing-aware capacity helper. This is directly relevant to PSC because PSC also cares about actual item/count context, not only `ThingDef`.
- **Stack Gap** uses stronger IL/private-path patching for hard cap behavior, including direct placement and recipe/product storage. That supports the claim that hard physical caps require deeper intervention than `AllowedToAccept` and final job clamping.
- **Stockpile Limit** transpiles the hidden `Toils_Haul.PlaceHauledThingInCell` lambda to intercept `Pawn_CarryTracker.TryDropCarriedThing`, which is exactly the kind of drop-path bypass PSC would face if it promises hard caps.
- **Storage Sorting** uses a current-thing search context plus a priority getter patch rather than a broad rewrite, proving that item-sensitive storage priority can be made to work with carefully scoped global context.
- **Variety Matters** mostly avoids transpilers for limits, but still patches absorption and haul job counts. This is evidence for a softer-cap path, not evidence that all transpilers are unnecessary.

So the risk model should distinguish between:

- **Unproven transpilers** that invent a new fragile rewrite.
- **Known-pattern transpilers** that copy an established storage-mod seam and change only PSC's policy helper.

The second category is still riskier than a postfix, but much less risky than "transpiler" sounds by itself. If PSC wants a behavior that LWM, Stack Gap, or Stockpile Limit already had to solve with IL, we should treat that as evidence that IL may be the mature path rather than a last resort.

### Best candidate: fine-order inside `TryFindBestBetterStoreCellFor`

This is the strongest case.

The blocker is vanilla's comparison:

```csharp
if ((int)priority < (int)foundPriority || (int)priority <= (int)currentPriority)
    break;
```

That line makes same-vanilla-band movement impossible. A transpiler could replace this comparison with a PSC-aware helper:

```csharp
if (PscOrder.ShouldBreakSearch(slotGroup, foundPriority, currentPriority, t, map))
    break;
```

or, more narrowly, replace only the `priority <= currentPriority` decision with a call that compares full PSC order when fine-order is active.

Why this may be better than prefix/postfix:

- It preserves vanilla's single-pass search.
- It avoids a second scan over groups/cells.
- It lets PSC participate in the same early-break behavior vanilla already uses.
- It solves `Preferred b -> Preferred a` directly.

Risk:

- Medium-high, because this is core hauling search IL.
- Must be version-gated and fail closed. If the expected instruction pattern is not found, disable fine-order and log one warning, not a stream of errors.

Verdict: **Most beneficial transpiler if subpriority/1-10 are meant to be real gameplay features.**

### Strong candidate: `HaulAIUtility.HaulToCellStorageJob` capacity calculation

Vanilla computes haul count by scanning destination cells and calling:

```csharp
cell.GetItemStackSpaceLeftFor(map, t.def)
```

LWM Deep Storage transpiles this because the method only receives a `ThingDef`, while deep storage capacity can depend on the actual `Thing`. PSC also has reason to care about the actual target unit's remaining PSC capacity, batch threshold, and source/destination policy.

A transpiler could replace the capacity call with:

```csharp
PscCapacity.GetCellSpaceLeftFor(cell, map, t, p)
```

Why this may be better than a postfix:

- The vanilla loop would accumulate PSC-aware space from the start.
- Final `job.count` would naturally reflect PSC capacity instead of being clamped after a possibly misleading vanilla total.
- Batch enforcement can become "the job never reaches enough capacity" rather than "make a job, then cancel or zero it."
- Multi-stack storage integration can be centralized.

Risk:

- Medium. LWM Deep Storage has already demonstrated this seam over multiple RimWorld versions, but it is still a hot hauling method.
- Compatibility with other mods also transpiling the same call must be tested. Harmony priority and call-chain composition matter.

Verdict: **Worth considering for M2 if postfix clamping causes bad jobs or repeated failed hauling.** Because LWM's established method is so close to PSC's need, this may be the best "proven risky" transpiler candidate rather than an experimental one.

### Strong candidate if hard caps are desired: direct placement

If PSC chooses a hard-cap contract, postfix admission is not enough. The key bypasses are:

- `Toils_Haul.PlaceHauledThingInCell`, where carried things are dropped.
- `Pawn_CarryTracker.TryDropCarriedThing`, depending on how broad the enforcement should be.
- `GenPlace.TryPlaceDirect`, where stacks merge or split onto a cell.
- recipe product placement paths.

Stack Gap and Stockpile Limit both show why people go here: direct placement can bypass storage planning.

Potential transpiler goal:

```csharp
TryDropCarriedThing(...) -> PscDrop.TryDropCarriedThing(...)
```

or:

```csharp
TryPlaceDirect(...) merge/split capacity -> PSC-aware remaining capacity
```

Why this may be better:

- It makes upper limits real hard caps.
- It prevents "PSC says max 20 but the game just dropped 75 there" moments.

Risk:

- High. These paths touch many systems: bills, manual drops, product placement, incomplete haul fallback, and modded carry behavior.
- Anti-softlock handling is required. If PSC blocks a drop, the pawn must have a valid alternative or a clean failure path.

Verdict: **Only worth it if PSC explicitly promises hard physical caps. Not recommended for M1.** If PSC later chooses hard caps, Stack Gap and Stockpile Limit should be treated as implementation precedents, not merely cautionary tales.

### Possible candidate: `NoStorageBlockersIn` capacity semantics

`NoStorageBlockersIn` is private and hot, but Harmony can still patch it by name. The research proposes a postfix, which is usually correct.

A transpiler becomes interesting only if PSC needs to alter vanilla's internal notion of "there is stack space here" instead of merely rejecting the final result. For example, replacing:

```csharp
c.GetItemCount(map) >= c.GetMaxItemsAllowedInCell(map)
```

with a PSC-aware capacity helper could avoid duplicate capacity work elsewhere.

Risk:

- Medium-high for compatibility. Many storage mods patch this method.
- A tighten-only postfix composes better with other mods.

Verdict: **Usually avoid. Use only if profiling shows repeated postfix checks are expensive or hard-cap semantics demand it.**

### Possible candidate: storage priority getter/context patch

Storage Sorting uses a current-thing context and priority getter patch to make priority item-sensitive. PSC could theoretically use a similar trick for fine-order or feeder routing.

This probably does **not** need a transpiler. A getter postfix can work if the current-thing context is carefully scoped around hauling search.

Risk:

- Hidden global state. If the current thing leaks outside the intended search, unrelated storage priority reads can be wrong.

Verdict: **Getter postfix/prefix-finalizer is enough; do not transpile unless forced.**

### Not worth transpiling in v1

- `StorageSettings.AllowedToAccept`: postfix is enough and composes well.
- `StorageSettings.ExposeData`: postfix is the correct persistence seam.
- `StorageSettingsClipboard.Copy/PasteInto`: postfix is enough, but deep-copy PSC data.
- `ListerHaulables`: better to trigger vanilla recalcs and maintain PSC's own demand index than to rewrite haulable listing.
- UI methods: fragile enough already; use normal Harmony patches and local drawing helpers where possible.

### Recommended loosened rule

Replace "no transpilers" with:

1. **No broad replacement transpilers in M1.**
2. **One narrow transpiler is acceptable when it replaces a single vanilla comparison or capacity call.**
3. **Every transpiler must be version-gated against RimWorld 1.6.4850 source shape.**
4. **Every transpiler must fail closed by disabling only the dependent PSC feature.**
5. **Every transpiler gets a dedicated dev-mode self-test.**

My ranking:

| Rank | Transpiler target | Benefit | Risk | Use when |
|---|---|---:|---:|---|
| 1 | `TryFindBestBetterStoreCellFor` priority break | Enables real fine-order without duplicate search | Med-high | M4 fine-order |
| 2 | `HaulToCellStorageJob` capacity call | Better counts, batch, multi-stack behavior; LWM precedent | Medium | M2 if postfix clamp is noisy |
| 3 | Direct drop / `GenPlace.TryPlaceDirect` | Hard caps; Stack Gap / Stockpile Limit precedent | High | Only after choosing hard-cap contract |
| 4 | `NoStorageBlockersIn` internals | Cleaner capacity semantics | Med-high | Only if postfix is too costly/late |

The big architectural point: transpilers are not automatically less performant or less compatible than postfixes. A bad transpiler is brittle; a good transpiler can avoid redoing vanilla's work. And when an older, respected storage mod has used a particular transpiler seam for years, that history should lower the estimated implementation risk. For PSC, the fine-order search comparison is the most PSC-specific candidate; the haul-count and direct-drop candidates are the most precedent-backed candidates.
