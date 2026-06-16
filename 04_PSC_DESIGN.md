# Precision Stockpile Control — Design Document

*RimWorld 1.6 (verified against 1.6.4850 decompiled source) · Harmony 2.4.x · `net48`*

This is the single authoritative design document for PSC. It supersedes `DESIGN_GOALS.md`, `RESEARCH.md`, and `RED_TEAM.md`. Vanilla claims are source-verified (file + method cited). Reference-mod claims are from the `Stockpile Manipulation` reference group (SatisfiedStorage, Stockpile Limit, Variety Matters Stockpile, Stack Gap, Storage Sorting, LWM Deep Storage).

---

## 1. Design Goals

### 1.1 Low performance impact
Stockpiles are ubiquitous. PSC must not run expensive checks on hauling hot paths. Core strategies: O(1) admission via count cache, early opt-out for vanilla stockpiles, demand index to skip PSC work entirely when no limited stockpile wants a given def, and staggered resync for self-correction.

### 1.2 Depth-scaling UI
PSC adds **one button** to the vanilla stockpile tab. Everything else is behind it. Effects already in play must be visible in the normal vanilla menu without requiring the player to re-open the PSC panel.

### 1.3 Save game compatibility
- **Remove:** limits degrade to plain allowed (unlimited). Unknown XML nodes are ignored by vanilla on load. No warnings after the first load.
- **Add:** PSC loads with no data, no dict entries, pure vanilla behavior until the player interacts with it.
- **Migration:** from other limit mods — later milestone (D8).

### 1.4 Stable mod integration
| Mod | Approach |
|---|---|
| LWM Deep Storage / Ogre / multi-stack | Read capacity live via `GetMaxItemsAllowedInCell` / `GetItemStackSpaceLeftFor`; never assume 1 stack/cell |
| Pick Up and Haul | Postfix `WorkGiver_HaulToInventory.CapacityAt`; soft-dep guarded |
| Flickable Storage (MIT) | Integrate on/off + receive-only directly (D9) |
| Material Filter (Harmony) — in-house | Share `DoThingDef` row; tab order `Priority · Letter · MFH · PSC-button` (D10) |
| Other limit mods | Migration deferred (D8) |

---

## 2. Locked Decisions

| ID | Decision |
|---|---|
| D1 | On PSC removal, a limited item degrades to plain **allowed** (unlimited). |
| D2 | **Hard cap contract.** Upper limit = the maximum. M2 realizes this as a **focused** hard cap (see §8.1 "M2 Hard Cap — Focused Scope"): the haul/drop path and Pick Up And Haul are capped; rare direct-spawn paths stay soft and are documented. UI uses "maximum" (not "target maximum") once M2 ships. |
| D3 | Click vs. click-drag handled like Material Filter (Harmony). |
| D4 | Auto-priority sets the **source** one fine-order step lower. Destination never modified. *(Implementation deferred to M4 — it needs the fine-order letter mechanism. M3 ships the `autosetSourcePriority` setting as a no-op and enforces link validity only: an invalid link draws red and is not a functional feeder edge.)* |
| D5 | Destination must outrank source — **enforced.** A link where destination ≤ source priority is non-functional (drawn red). Prevents cycles and reverse-grab. |
| D6 | 1–10 priority is **distinct when active** (realized via the fine-order machinery, §6.4); collapses to the 5 vanilla bands when the setting is off or the mod is removed. Internal `StoragePriority` enum never changes. |
| D7 | Feeder endpoint = the **haul unit**: `member.Group` when linked, else the standalone slot group / zone. Stored by `GetUniqueLoadID()` string handle; resolves lazily on load. |
| D8 | Migration from other limit mods = later milestone. |
| D9 | Flickable Storage is MIT → integrate on/off + receive-only directly. |
| D10 | MFH stays; tab top-row order `Priority · Letter · MFH · PSC-button`. |
| D11 | "Only from source" = accept only items currently residing in a linked source unit; never haul loose or ground items in. |
| D12 | Batch waits for the full batch size. Gate in `AllowedToAccept`: `t.stackCount ≥ batch`. Cancel job in `HaulToCellStorageJob` postfix if `final job.count < batch`. |
| D13 | PSC never changes per-def `stackLimit`; reads it live. |
| D14 | ~~Subpriority and 1–10 via postfixes only.~~ **REVOKED.** Fine-order requires a narrow transpiler on `TryFindBestBetterStoreCellFor`'s priority break comparison (see §6.4). Postfix-only cannot express same-band movement when an item is already stored in that band. |
| D15 | **Lower limit is nullable.** `null` = always refill until upper (default). `0` = refill only when empty. `N` = refill when count ≤ N. The slider has "always" as a distinct leftmost position (null), then 0, then N up to upper. |
| D16 | **Feeder source/target context guard.** Feeder movement rules (`onlyFromSource`, `onlyToDestinations`) apply only when the item's current haul unit differs from the candidate target unit. A source stockpile always accepts its own contents for validity checks. |
| D17 | **Demand index** (`ThingDef → list of PSC units currently accepting that def`) maintained as part of M1 core. Enables fast early-out on hot path before `AllowedToAccept` work begins. |
| D18 | **Cache patches are M1 scope**, not optional polish: `Thing.SplitOff`, `Thing.TryAbsorbStack`, `Zone_Stockpile.AddCell`/`RemoveCell`, `StorageGroupUtility.SetStorageGroup` (link/unlink). Staggered resync is a self-healing backstop, not the primary mechanism. |

---

## 3. What "Priority" Is in Code

`StoragePriority` is a `byte` enum: `Unstored=0, Low=1, Normal=2, Preferred=3, Important=4, Critical=5` (`RimWorld/StoragePriority.cs`). Only `Low`–`Critical` are user-selectable (5 bands).

`HaulDestinationManager.AllGroupsListInPriorityOrder` is re-sorted **only** on `Notify_HaulDestinationChangedPriority` — never per tick. `TryFindBestBetterStoreCellFor` walks the list top-down and **breaks** the moment a group's band drops below the best band found so far. Within the top band, proximity is the only vanilla tiebreak.

**Consequence for fine-order:** vanilla offers no ordering finer than those 5 bands. PSC's subpriority and 1–10 half-steps are a PSC-owned fine-order layer. Because vanilla breaks the search when `priority <= currentPriority`, a same-band move (e.g. `Preferred b → Preferred a`) is invisible to a postfix on the outer method — the postfix fires after vanilla already returned false. This is why D14 is revoked and a transpiler is required.

---

## 4. State and Persistence Architecture

### 4.1 Limit / policy / fine-order state → attached to `StorageSettings`

```
static Dictionary<StorageSettings, PSC_StorageData> Map   // rebuilt each load
PSC_StorageData : IExposable                               // scribed by ExposeData postfix
```

Scribed via postfix on `StorageSettings.ExposeData` as a child node (`<psc>`). Unknown XML nodes are ignored on vanilla load → removal-safe. No dict entry → pure vanilla behavior → add-safe.

`StorageGroup` owns one shared `StorageSettings` (`StorageGroup.cs:6,15`). Linked members defer to it, so keying on `StorageSettings` shares limits and fine-order across a linked group automatically and correctly. *Caveat: link/unlink swaps which settings object is active; PSC must follow vanilla's settings-transfer on link/unlink/blueprint/frame.*

`PSC_StorageData` holds:
- Per-def limits (`lower?`, `upper?`)
- Per-def refilling state (runtime, recomputable; not scribed)
- Batch size
- Fine-order key `(vanilla band, 1–10 sub-tier, a-z letter)`
- Feeder policy flags (`onlyFromSource`, `onlyToDestinations`)

### 4.2 Feeder links → `PSC_MapComponent`

Feeder links bind haul units (D7), which a shared `StorageSettings` cannot express. Stored as pairs of stable string handles (`GetUniqueLoadID()`). Resolved lazily on load and pruned on endpoint lifecycle changes; an unresolved endpoint (removed storage or mod) silently drops the link — no warning spam, no polymorphic-collection unload hazard. When a unit loses its last incoming/outgoing link, the matching `onlyFromSource` / `onlyToDestinations` flag auto-clears so the storage remains reusable.

### 4.3 Demand index → `PSC_MapComponent`

```
Dictionary<ThingDef, List<PSC_HaulUnit>> demandIndex   // units currently accepting that def
bool anyPscActive                                        // per-map early-out flag
```

Updated whenever a unit's refilling state flips or limits change. On the hot path: if `anyPscActive == false`, skip all PSC work. If the target unit has no demand entry for `t.def`, return vanilla result.

### 4.4 Explicitly avoided

No `Zone_Stockpile` subclass. No `Scribe_References` to storage endpoints. No static fields for runtime state beyond rebuildable dictionaries.

---

## 5. The Haul Unit Resolver

Because `StorageGroup` is `IStoreSettingsParent` and `ISlotGroup` but **not** `ISlotGroupParent`, `settings.owner.GetSlotGroup()` does not work generically. PSC needs one canonical resolver, not scattered per-patch logic:

```csharp
PSC_HaulUnit ResolveSettingsHaulUnit(StorageSettings settings)
// StorageGroup owner        → the storage group itself
// ISlotGroupParent owner    → its StorageGroup if linked, else its slot group
// null / fixed parent       → no PSC unit (out of scope)

PSC_HaulUnit ResolveCurrentHaulUnit(Thing t)
// t.Spawned                 → StoreUtility.GetSlotGroup(t), then resolve as above
// carried / unspawned       → null (loose item; feeder gates do not apply)
```

`StorageGroup.CellsList` returns a **static temporary list** — do not retain, store, or call a second time while iterating a previous result.

---

## 6. Performance Strategy

### 6.1 Count cache

```
PSC_StorageData {
    Dictionary<ThingDef, int> counts;    // live item count per def in this unit — O(1) lookup
    HashSet<ThingDef> refilling;         // hysteresis state — runtime, recomputable
}
```

**Incremental updates** via postfixes on `Notify_ReceivedThing` / `Notify_LostThing` (both `Building_Storage` and `Zone_Stockpile` implement `ISlotGroupParent`). These fire on spawn/despawn — whole-thing removals are covered.

**Known drift sources that require explicit patches (D18):**
- `Thing.SplitOff`: reduces `stackCount` without despawning; dirty affected unit/def.
- `Thing.TryAbsorbStack` / `ThingUtility.TryAbsorbStackNumToTake`: increases one stack, reduces/destroys another; dirty both affected units/defs.
- `Zone_Stockpile.AddCell` / `RemoveCell`: add/subtract actual contents of the added/removed cell.
- `StorageGroupUtility.SetStorageGroup` (link/unlink): rebuild counts for old and new units from their current `HeldThings`.

**Staggered resync** in `PSC_MapComponent.MapComponentTick` (every 250 ticks, tracked units only, dirty defs only) is a self-healing backstop for any drift source not explicitly patched — not the primary mechanism.

### 6.2 Opt-in early-out (single most important rule)

A stockpile with no PSC data pays one dictionary lookup. Every PSC patch begins:

```csharp
if (!anyPscActive) return;
data = Map.TryGetValue(settings);
if (data == null) return;
```

The demand index adds a second gate: if the target unit has no demand entry for `t.def`, skip PSC work for that def.

### 6.3 No per-check allocations

Reuse cached collections and `Clear()`. No LINQ on hot paths.

### 6.4 Reservation overshoot (accepted, v1)

Without counting in-flight reservations, multiple pawns can briefly overshoot the upper limit, after which admission stops. Variety Matters adds reservation counting — deferred unless profiling shows it matters.

---

## 7. Seam Map

| PSC feature | Vanilla seam | Verified location | Patch type |
|---|---|---|---|
| Attach per-storage state | `StorageSettings.ExposeData` | `RimWorld/StorageSettings.cs:78` | Postfix (child node) |
| Carry state on copy/paste | `StorageSettingsClipboard.Copy` / `PasteInto` | — | Postfix (deep-copy PSC data) |
| Stockpile-wide admission (upper/lower/hysteresis/batch/feeder) | `StorageSettings.AllowedToAccept(Thing)` | `RimWorld/StorageSettings.cs:97` | Postfix, tighten-only |
| Clamp haul amount / cancel under-batch jobs | `HaulAIUtility.HaulToCellStorageJob` | `Verse.AI/HaulAIUtility.cs:141` | Postfix (clamp or null `__result.count`) |
| Count maintenance (zones) | `Zone_Stockpile.Notify_ReceivedThing` / `Notify_LostThing` / `AddCell` / `RemoveCell` | — | Postfix |
| Count maintenance (buildings) | `Building_Storage.Notify_ReceivedThing` / `Notify_LostThing` | — | Postfix |
| SplitOff drift correction | `Thing.SplitOff` | — | Postfix (dirty unit/def) |
| Absorption drift correction | `Thing.TryAbsorbStack` | — | Postfix (dirty both units/defs) |
| Link/unlink count rebuild | `StorageGroupUtility.SetStorageGroup` | — | Postfix (rebuild from HeldThings) |
| Fine-order within band | `StoreUtility.TryFindBestBetterStoreCellFor` | `RimWorld/StoreUtility.cs:173` | **Narrow transpiler** — replace `priority <= currentPriority` break with `PscOrder.ShouldBreakSearch(...)` |
| Fine-order list sort | `HaulDestinationManager.CompareSlotGroupPrioritiesDescending` | `RimWorld/HaulDestinationManager.cs:153` | Postfix (tie-break by fine-order key) |
| PUAH count clamp | `WorkGiver_HaulToInventory.CapacityAt` | reference mod | Postfix, soft-dep guarded |
| Tab top row (PSC button) | `ITab_Storage.FillTab` + `ThingFilterUI.DoThingFilterConfigWindow` | `RimWorld/ITab_Storage.cs:104`, `Verse/ThingFilterUI.cs:29` | Postfix button in a reserved row under priority; prefix shifts vanilla filter controls down |
| Per-item row (limit label, I-beam, click/drag) | `Listing_TreeThingFilter.DoThingDef` / `DoCategory` | — | Postfix |
| **M2: Hard cap on carry drop** | `Toils_Haul.PlaceHauledThingInCell` / `Pawn_CarryTracker.TryDropCarriedThing` | — | Transpiler (Stack Gap / Stockpile Limit precedent) |
| **M2: Hard cap on direct placement** | `GenPlace.TryPlaceDirect` | — | Transpiler (Stack Gap precedent) |

> Every M1 admission and ordering seam except fine-order is a postfix. The fine-order transpiler replaces a single comparison in one method. M2 hard-cap transpilers are deferred.

### Why `AllowedToAccept(Thing)` (`:97`) and not `AllowedToAccept(ThingDef)` (`:114`)

`TryFindBestBetterStoreCellForWorker` (`StoreUtility.cs:215`) calls `:97` once per candidate group before scanning cells — the correct granularity for stockpile-wide limits. `:114` is the scan/UI path; PSC leaves it untouched so defs are not hidden from the filter UI.

---

## 8. Admission Decision (Single Postfix, All Rules)

```
AllowedToAccept(Thing t) postfix — tighten-only (never override vanilla false):

if vanilla false:                                          return
data = demand index lookup; if no rule for t.def:          return
n    = data.counts[t.def]                                  // O(1)

// Hard cap — upper
if upper set and n >= upper:                               result = false; return

// Lower / hysteresis (D15)
if lower is null:                                          // always refilling — no gate
elif not data.refilling[t.def]:                            result = false; return
    // (refilling flips on at count ≤ lower, off at count ≥ upper)

// Batch (D12)
if batch set and t.stackCount < batch:                     result = false; return

// Feeder (D11, D16)
source = ResolveCurrentHaulUnit(t)                         // null = loose/unspawned/carried
target = ResolveSettingsHaulUnit(settings)
if source != target:
    hasEdge = source != null and HasFunctionalEdge(source, target) // stored edge + destination outranks source
    if source == null and activeFeederHaulContext(t, target):
        hasEdge = true                                    // carried item still has planned source
    if data.onlyFromSource and not hasEdge:                        result = false; return
    if source has onlyToDestinations and not hasEdge:               result = false; return

HaulToCellStorageJob postfix:
    if planned feeder edge: register runtime source->target context; disable opportunistic duplicates
    clamp job.count = min(job.count, upper - n)
    if batch set and job.count < batch:   job = null (cancel)
```

**Drop-time note (D2):** `AllowedToAccept` is only a *planning* gate — carried things are unspawned and vanilla attempts `TryDropCarriedThing` regardless. M1 used this gate alone, so the cap was soft ("target maximum"). M2 adds drop-time enforcement (§8.1) and the UI now says "maximum."

### 8.1 M2 Hard Cap — Focused Scope (as built)

M2's hard cap is **focused** (a deliberate scope choice over a full placement-path rewrite). It makes the upper limit a real maximum for the **normal colony flow** while leaving a small, documented set of edge paths soft.

**Mechanism — per-unit live-count cap at the drop seam.** PSC limits are per *haul unit*; every vanilla/precedent capacity seam is per *cell*. So PSC enforces at the carry-drop choke point using the live unit count from the M1 count cache:

- **Carry-drop cap** — prefix on `Pawn_CarryTracker.TryDropCarriedThing(IntVec3, ThingPlaceMode, out Thing, Action<Thing,int>)` (the no-count overload). When dropping into a PSC-capped unit, drop only `room = max(0, upper − liveUnitCount)` via the count overload (which routes through `innerContainer.TryDrop`, not back through the patched method — no recursion), leaving the remainder carried for vanilla `PlaceHauledThingInCell`'s existing fallback (find better storage → haul aside → last-resort). This is chosen over the design's `PlaceHauledThingInCell`-lambda transpiler (Stockpile Limit precedent): a tightly-gated prefix on the public method is more robust (no compiler-lambda reflection), version-resilient, and also catches manual/drafted drops into a capped stockpile.
- **No haul loop.** Admission (`AllowedToAccept`) and the drop prefix read the **same** live count cache, so planning and enforcement are consistent: once a unit is full, no new haul-to-it jobs are created, so a reduced/zero drop never feeds a re-haul loop. The carry-drop prefix is the *true* enforcement against reservation overshoot, opportunistic duplicates, and plan-vs-drop drift that the M1 `job.count` clamp alone cannot stop.
- **Pick Up And Haul** — postfix on `WorkGiver_HaulToInventory.CapacityAt` (soft-dep via `Prepare()`/reflection) reduces reported capacity to the unit room. Best-effort: PUAH probes multiple cells before executing, so transient multi-cell overshoot within one unit is possible and self-corrects once the unit hits the cap and the drop prefix blocks further drops.
- **Multi-stack (LWM / Ogre)** — free: PSC counts and caps are in **items** (via `HeldThings`), and PUAH already queries `IHoldMultipleThings` capacity first. Nothing assumes one stack per cell.

**What is left soft (documented limitation):** items spawned **directly** into storage rather than hauled in — e.g. a workbench standing inside the stockpile dropping its product on the spot, map-generation scatter, or other mods spawning via `GenPlace.TryPlaceDirect` — bypass the carry-drop seam and can momentarily exceed the maximum. Normal crafting is **not** in this set: bill products spawn at the bench and are then *hauled* to storage, so they pass through the capped haul/drop path. Closing the direct-spawn gap would require patching `GenPlace.TryPlaceDirect` (per-unit live-count cap with spill handling) and is deferred; it can be added later without disturbing the focused seams.

**Batch (D12), as built:** two gates — a source-stack gate in `AllowedToAccept` (`t.stackCount < batch ⇒ reject`) and a final-count cancel in `HaulToCellStorageJob` (`job.count < batch ⇒ null`). Consequence: when remaining room `< batch` (a near-full capped unit), the last `< batch` items are intentionally not hauled ("no small trips"). Opportunistic duplicates are left at vanilla behavior.

---

## 9. Fine-Order Architecture (D14 revised)

### The core problem

Vanilla `TryFindBestBetterStoreCellFor` initializes `foundPriority = currentPriority` and breaks when `(int)priority <= (int)currentPriority`. If an item is already in a Preferred stockpile, vanilla never searches other Preferred stockpiles. A postfix on the outer method fires after vanilla returned false — the same-band candidate was never examined. No postfix can recover it.

### Solution: narrow transpiler

Replace the single break comparison:

```csharp
// vanilla
if ((int)priority < (int)foundPriority || (int)priority <= (int)currentPriority)
    break;

// PSC replacement (when fine-order active)
if (PscOrder.ShouldBreakSearch(slotGroup, foundPriority, currentPriority, t, map))
    break;
```

`PscOrder.ShouldBreakSearch` mirrors vanilla logic when fine-order is inactive (exact same result, no behavior change). When active, it permits continuing into groups that share the item's current vanilla band, but only if they outrank the current unit by full PSC key `(vanilla band, sub-tier, letter)`.

For `Low` band, `currentPriority` must be temporarily treated as `Unstored` to permit the search floor; the helper handles this.

**Version-gate:** match against 1.6.4850 IL shape. If the pattern is not found, log one warning and disable fine-order (fail closed — no stream of errors).

**Performance:** guarded behind `anyFineOrderActive` per-map bool. When inactive, the transpiler's helper call collapses to the same branch vanilla takes. When active, the search iterates only groups in the current band (bounded) and stops at the first that outranks the source — no full-group scan.

Call `Notify_HaulDestinationChangedPriority` whenever a unit's fine-order key changes, or the sorted list goes stale.

### Fine-order key

```
(StoragePriority band, byte subTier, char letter)
```

`band` = untouched vanilla enum. `subTier` + `letter` are PSC's. Full comparison: band descending, subTier ascending (1 before 2), letter ascending (a before b).

### 1–10 mapping

| # | Vanilla band | Sub-tier |
|---|---|---|
| 1 | Critical | 1 |
| 2 | Critical | 2 |
| 3 | Important | 1 |
| 4 | Important | 2 |
| 5 | Preferred | 1 |
| 6 | Preferred | 2 |
| 7 | Normal | 1 |
| 8 | Normal | 2 |
| 9 | Low | 1 |
| 10 | Low | 2 |

**Collapse (mod removed / setting off):** drop sub-tier → pairs merge to band. `1,2→Critical · 3,4→Important · 5,6→Preferred · 7,8→Normal · 9,10→Low`. Vanilla enum unchanged; safe.

**Display anchors:** Critical→1, Important→3, Preferred→5, Normal→7, Low→10. On enabling 10-mode, existing vanilla stockpiles land at {1,3,5,7,10}; the newly reachable levels are {2,4,6,8,9}.

a-z subpriority stacks below sub-tier: full key `(band, sub-tier, letter)`. Reverse-order setting is a UI label flip only; internal ordering unchanged.

---

## 10. UI Specification

### 10.1 Depth-scaling entry point

One PSC button on the stockpile tab, placed in a reserved row under the vanilla Priority button and above Clear all / Allow all. Opens the PSC control window. No PSC controls are visible at the vanilla level except effects already applied (see §10.4).

### 10.2 Limit input

Limits tracked in items; stacks are display only.

Format: `A (B) — X (Y)` where A = lower limit, B = `ceil(A / stackLimit)`, X = upper limit, Y = `ceil(X / stackLimit)`. If one side is unset: `A (B) —` or `— X (Y)`.

**Slider:** left toggle switches between items mode and stacks mode. The editor uses a dual-handle range control with direct entry boxes on the left (lower) and right (upper). Blank lower = always refill (null); blank upper = fill to current maximum (null).

- *Items mode:* smooth from 0, with "stickiness buffer" of ~10% of stack size at each stack boundary (e.g. for stackLimit 75: smooth 0→75, then resist until 75+7 to cross to 76) so players land on round-stack values naturally.
- *Stacks mode:* integer stacks. Far-right buffer = current maximum stack count; rightmost = null upper (fill to maximum as storage changes).
- Type directly in either entry box; deleting the entry sets that side to null.
- Multi-def editors with mixed child stack sizes force stacks mode and explain why items mode is disabled.
- Stack-mode maximum is based on live physical stack slots: sum each storage cell's `GetMaxItemsAllowedInCell(map)`, not just cell count, with current held stacks as a fallback.

**Lower limit slider positions (D15):**
- Leftmost = "always" (null) — always refilling.
- Next = 0 — refill only when empty.
- Then N, up to upper − 1.

### 10.3 Batch fill

Separate slider/input below the search-apply buttons. Independent of limits. "Never haul fewer than N items in one trip."

### 10.4 Visible effects in vanilla menu (depth-scaling feedback)

Per-item row additions (via `DoThingDef` postfix, mirroring MFH):
- Label showing current limits near the vanilla checkbox in the same `A (B) - X (Y)` item/stack format as the main editor when stack context is available, with the tooltip acting as the overflow/fallback.
- Left-click on a PSC-limited row's I-beam opens the per-item limit submenu (reproduces the global input, adds Apply / Cancel); right-click remains a compatibility path.
- A yellow I-beam replaces the vanilla checkbox for PSC-limited item rows. The marker owns left-click and left-drag, while untouched rows keep vanilla toggling and paint-drag.
- I-beam symbol for mixed-limit category state, replacing vanilla's yellow tilde.
- Small green ✓ / red ✗ buttons to remove limits and allow / disallow.

Left-drag from a PSC-limited marker propagates that limit across rows by preserving stack ratios, not raw item counts (e.g. 2.5 source stacks becomes 2.5 target stacks using the target def's live `stackLimit`). Vanilla left click-drag propagates allow/disallow only across untouched rows; right-drag remains a PSC compatibility path.

Categories propagate naturally: if all currently allowed storable children share the same non-null limit, show it on the category. If allowed children mix null and set limits or disagree, show the I-beam only. Disallowed children do not affect the displayed PSC category state. Changing a category limit applies to all storable children allowed by the parent filter.

Vanilla "Clear all" / "Allow all" keep vanilla behavior (disallow or allow all) and clear per-def PSC limits for that storage. Stockpile-wide PSC policy such as batch is left intact.

### 10.5 Search integration

"Apply to search" and "Remove from search" buttons at the top of the PSC window apply or remove the selected limit to all items matching the current vanilla search panel filter.

The global PSC control window closes automatically when its source stockpile/storage is no longer selected, preventing stale edits from applying to the wrong storage.

### 10.6 Feeder link UI

**As built (M3):** six gizmos on the selected stockpile/shelf (single-selection gated): **Connect
source**, **Connect destination**, **Only from source** (toggle), **Only to destinations** (toggle),
**Show connections** (toggle), **Clear all connections** (right-click float-menu required — a plain
click only shows a hint). The two strictness toggles moved from the control window to gizmos to keep
all feeder controls together; each is grayed out until the unit has a source/destination, and is
seeded from a mod-setting default (both default on) when the unit gains its first link.

Connect source/destination are paint `Designator`s (no draw style): a single click links one storage;
a click-drag *paints* — every storage the cursor passes over is linked immediately (not a box drawn
out and applied on release), so the player can run across several stockpiles to link each.

Overlay: bright green arrows with directional arrowheads near both source and destination. Non-
functional links (destination priority ≤ source) drawn in red with small × marks along the line
(arrows kept). Drawn from `PscMapComponent.MapComponentUpdate`: the selected storage's links always,
or every link on the map when **Show connections** is on.

### 10.7 Subpriority display

Small letter box placed beside the vanilla priority box (not shrinking it — `FillTab` layout is left alone). No letter = highest within band. a–z sorts within band, a before b.

### 10.8 Priority numbering (optional tweak)

1–10 system with band anchors at {1,3,5,7,10}. Reverse-order is a UI label flip only. Collapsing back to vanilla: {1,2}→Critical, {3,4}→Important, {5,6}→Preferred, {7,8}→Normal, {9,10}→Low.

---

## 11. Feature Reference

### 11.1 Stack limits

Core feature. Tracks items (not stacks); stacks are display format. Per-def upper and lower limits. Categories propagate limits down.

On PSC removal: limited items degrade to plain allowed (D1).

### 11.2 Batch filling

"Never bring fewer than N." Enforced at two points: `AllowedToAccept` (source stack gate) and `HaulToCellStorageJob` (final count gate). Independent of limits.

### 11.3 Feeder stockpiles

Source → destination flow enforcement. Endpoints are haul units (D7). `onlyFromSource` and `onlyToDestinations` toggles. Auto-priority sets source one fine-order step lower (D4). Destination must outrank source (D5). Links stored by load-ID string handles; unresolved handles drop silently.

Feeder hauling carries runtime-only source→destination context on the hauled `Thing`, transferred to split carried stacks. This is required because vanilla revalidates storage after pickup, when the item is carried/unspawned and no longer resolves to its source slot group. Feeder-routed jobs disable opportunistic duplicate pickup in M3.

Feeder rules never apply when source unit == target unit (D16).

**Circular flow prevention:** enforced by D5. Destination must outrank source, so cycles are structurally impossible.

### 11.4 Subpriority (a-z)

Within-band ordering using fine-order key. Letter box beside priority box. No letter = highest within tier. Vanilla "link storage" linking of subpriorities is off by default (mod setting).

### 11.5 Priority numbering (1-10)

Optional tweak. Two sub-tiers per vanilla band. Distinct when active; collapses cleanly on removal or setting change (D6). Reverse-order is UI-only.

### 11.6 Copy/paste

PSC settings (limits, batch, feeder flags, fine-order) carry on vanilla copy/paste via `StorageSettingsClipboard` postfixes. Deep-copy PSC data; do not share references between stockpiles.

---

## 12. Mod Integration Details

### Pick Up and Haul (PUAH)

Postfix `WorkGiver_HaulToInventory.CapacityAt` (Stack Gap pattern). Soft-dep guarded via `[HarmonyPrepare]` check. Reduces reported capacity to respect PSC upper limit for the destination unit.

### LWM Deep Storage / Ogre / multi-stack

All capacity reads go through `GetMaxItemsAllowedInCell(map)` / `GetItemStackSpaceLeftFor(cell, map, def)`. PSC never assumes 1 stack/cell or hardcodes vanilla stack sizes. Counts are in items throughout.

### Flickable Storage (D9)

Integrate on/off and receive-only behaviors directly. Note interaction with feeder `onlyFromSource` / `onlyToDestinations` toggles — document behavior when both systems restrict a stockpile simultaneously.

### Material Filter (Harmony) (D10)

Share `DoThingDef` row space. Filters compose: material AND count. Tab order: `Priority · Letter · MFH · PSC-button`.

---

## 13. Risk Register

| Risk | Severity | Mitigation |
|---|---|---|
| Fine-order transpiler breaks on version change | **High** | Version-gate against 1.6.4850 IL; fail closed; log one warning; disable fine-order only |
| Fine-order transpiler conflicts with other hauling transpilers | Medium | Test Harmony priority and call-chain composition; Storage Sorting precedent |
| Count cache drift from unpatched drift sources | **High** | All known drift sources patched in M1 (D18); staggered resync as backstop |
| Feeder `onlyToDestinations` making source reject own contents | **High** | D16 context guard; test case required before M3 ships |
| Hard cap not enforced until M2 (direct-drop bypass) | Medium | **Resolved in M2 (focused, §8.1):** haul/drop + PUAH capped via `TryDropCarriedThing` prefix. Direct-spawn paths (`GenPlace.TryPlaceDirect`) remain soft — documented in README + UI footnote |
| Cancelling prefix on hot `TryDropCarriedThing` (M2) | Medium | `PscStorageDataStore.IsEmpty` int-compare early-out first, then a tight cell→unit→has-limit gate; faithfully replicates the drop (`innerContainer.TryDrop` via the count overload). Stockpile Limit precedent does the identical reduced drop |
| Reservation overshoot past upper limit | Low | Self-corrects via drain; reservation counting deferred |
| `StorageGroup.CellsList` retained after iteration | Low | Helper never retains; only iterates immediately or via `HeldThings` |
| Batch undershoot from opportunistic-duplicate aggregation | Low | Final job count cancel gate (D12); WorkGiver gate deferred |
| Fine-order search over-scans top band | Low | Stop at first outranking group with valid cell; `anyFineOrderActive` guard |

---

## 14. Required Test Scenarios

These must pass before any milestone is considered shippable.

**M1 (core limits):**
- Add PSC to existing save with 100 vanilla stockpiles: no PSC data created, no measurable hauling slowdown.
- One shelf, upper 20, no lower: accepts until 20.
- One shelf, upper 20, lower 5: stops at 20, restarts at 5 (not before).
- One shelf, upper 20, lower 0: stops at 20, restarts only when empty.
- Partial consumption via `SplitOff` updates count without waiting for resync.
- Stack absorption changes cached counts for both affected units immediately.
- Link two shelves with existing contents: group count exact immediately after linking.
- Unlink a shelf with existing contents: both old group and new member counts exact.

**M2 (hard caps + batch + PUAH):**
- Manual drop over target maximum is blocked (not just not-hauled-in).
- Bill product landing over cap is blocked or redirected.
- Batch 10: no final job count below 10 reaches the pawn.
- PUAH does not collect more than the PSC cap for a destination.
- LWM/Ogre multi-stack cells report physical capacity correctly.

**M3 (feeder):**
- Feeder source with `onlyToDestinations` still considers its own contents valid.
- Feeder destination with `onlyFromSource` rejects loose items but accepts linked-source items at planning time.
- Feeder opportunistic duplicates cannot collect from unlinked sources.
- Non-functional link (destination ≤ source priority) draws red; no haul jobs generated.

**M4 (fine-order):**
- `Preferred b → Preferred a` move is selected; `Preferred a → Preferred b` is not.
- Low-band fine-order works (Unstored as comparison floor).
- Fine-order transpiler absent (version mismatch): one log warning, fine-order disabled, no stream of errors.

---

## 15. Milestone Slicing

| Milestone | Contents |
|---|---|
| **M1 — Core limits** | `StorageSettings` attached data + ExposeData/clipboard + count cache + demand index + all Notify hooks + `SplitOff` / absorption / AddCell / RemoveCell / link-unlink patches + `AllowedToAccept` upper/lower/hysteresis + `HaulToCellStorageJob` clamp+cancel + PSC side button + control window + per-row UI |
| **M2 — Hard caps + batch + integration** | **Focused hard cap (§8.1):** per-unit live-count cap via a `Pawn_CarryTracker.TryDropCarriedThing` prefix (covers hauling, manual/drafted drops, and bill products hauled from benches); batch source-stack + final-count enforcement; PUAH `CapacityAt` postfix (soft-dep); LWM/Ogre capacity correctness (free — counts in items); UI "target maximum" → "maximum". **Left soft:** direct-spawn into storage (`GenPlace.TryPlaceDirect` — workbench-in-stockpile, map-gen, mod spawns), deferred |
| **M3 — Feeder links** | `PSC_MapComponent` link store; feeder rules in admission with D16 context guard; Contagion-style overlay; auto-priority; opportunistic-duplicate control |
| **M4 — Fine-order** | Narrow transpiler on `TryFindBestBetterStoreCellFor`; `CompareSlotGroupPrioritiesDescending` postfix tie-break; subpriority letter box UI; 1-10 relabel UI; `anyFineOrderActive` guard; dev-mode self-test and debug overlay |
| **M5 — Migration + Flickable** | Import other mods' limits; Flickable Storage integration |

---

*Source verification: vanilla signatures confirmed against `Rimworld 1.6.4850 Decompiled Source` via the `rimworld-source` MCP. Reference-mod seams from the `Stockpile Manipulation` reference group.*
