# PSC — Implementation Research

*RimWorld 1.6 (verified against 1.6.4850 decompiled source) · Harmony 2.4.x · `net48`*

Companion to `DESIGN_GOALS.md`. This is the "how does it actually hook into vanilla" document: the seams, the performance model, and the risk register. Vanilla claims are source-verified (file + method cited). Reference-mod claims are from the `Stockpile Manipulation` reference group.

---

## 1. Locked decisions

| # | Decision |
|---|---|
| D1 | On removal, a limited item degrades to plain **allowed** (unlimited). |
| D2 | Upper limit = **stop accepting**; over-limit contents drain naturally (no active eviction). |
| D3 | Click vs click-drag handled like Material Filter (Harmony) — solved precedent. |
| D4 | Auto-priority sets the **source** one fine-order step lower (`5a` pulls from `5b` pulls from `5c`). Destination never modified. |
| D5 | Destination must outrank source — **enforced**. A link where destination ≤ source priority is non-functional (drawn red). Prevents cycles and reverse-grab. |
| D6 | 1–10 priority is **distinct when active** (realized via the fine-order machinery, §6.4); it **collapses** to the 5 vanilla bands when the setting is off or the mod is removed. Internal `StoragePriority` enum never changes. |
| D7 | Vanilla "link storage" = `StorageGroup` (building storage only; ground zones aren't members). PSC limit/policy settings carry across it for free — **verified**: `Building_Storage.GetStoreSettings()` returns `storageGroup.GetStoreSettings()` when linked (`Building_Storage.cs:100`), so linked members share one settings object by reference. Feeder links bind to the **haul unit** (§6.3), not individual members. *Caveat: link/unlink swaps which settings object is active; PSC must follow vanilla's settings-transfer on link/unlink/blueprint/frame.* |
| D8 | Migration from other limit mods = **later milestone**. |
| D9 | Flickable Storage is MIT → integrate on/off + receive-only directly. |
| D10 | MFH stays; tab top-row order `Priority · Letter · MFH · X`, PSC button **below**. |
| D11 | "Only from source" = accept **only** items currently residing in a linked source unit; never haul loose/ground items in. |
| D12 | Batch waits for the full batch size — no top-off exception. **Cheap gate accepted** (§5): `stackCount ≥ batch` + `room ≥ batch`. |
| D13 | PSC never changes per-def `stackLimit`; reads it live. |
| D14 | Subpriority (a-z) and the 1–10 half-steps are **one fine-order value** below the vanilla band, realized with **postfixes, no transpiler** (§6.4). |

---

## 2. The seam map — what code we hook

| PSC feature | Vanilla seam | Verified location | Patch type |
|---|---|---|---|
| **Attach per-storage state** | `StorageSettings.ExposeData` | `RimWorld/StorageSettings.cs:78` | Postfix (scribe child node) |
| **Carry state on copy/paste** | `StorageSettingsClipboard.Copy` / `PasteInto` | — | Postfix |
| **Stockpile-wide admission** (upper, lower/hysteresis, batch, feeder) | `StorageSettings.AllowedToAccept(Thing)` | `RimWorld/StorageSettings.cs:97` | Postfix, **tighten-only** |
| **Per-cell admission** (fallback / multi-stack capacity) | `StoreUtility.NoStorageBlockersIn` | `RimWorld/StoreUtility.cs:117` (private static) | Postfix, tighten-only |
| **Clamp haul amount** | `HaulAIUtility.HaulToCellStorageJob` | `Verse.AI/HaulAIUtility.cs:141` | Postfix (clamp `__result.count`) |
| **Fine-order within band** (subpriority + 1–10 half-steps) | `StoreUtility.TryFindBestBetterStoreCellFor` | `RimWorld/StoreUtility.cs:173` | **Postfix** (re-select within band) |
| **Fine-order list sort** | `HaulDestinationManager.CompareSlotGroupPrioritiesDescending` | `RimWorld/HaulDestinationManager.cs:153` (private static) | Postfix (tie-break) |
| **Count maintenance (zones)** | `Zone_Stockpile.Notify_ReceivedThing` / `Notify_LostThing` / `AddCell` / `RemoveCell` | — | Postfix |
| **Count maintenance (buildings)** | `Building_Storage.Notify_ReceivedThing` / `Notify_LostThing` | — | Postfix |
| **PUAH count clamp** | `WorkGiver_HaulToInventory.CapacityAt` (PUAH) | reference mod | Postfix, soft-dep guarded |
| **Tab top row** (subpriority box, PSC button) | `ITab_Storage.FillTab` | `RimWorld/ITab_Storage.cs:104` | Postfix (capture `SelStoreSettingsParent` + draw) |
| **Per-item row** (limit label, I-beam, right-click) | `Listing_TreeThingFilter.DoThingDef` / `DoCategory` | — | Prefix/Postfix |

> Every admission and ordering hook is a **postfix**. No transpiler is required anywhere in v1 (D14). Transpilers remain only a contingency if a postfix proves insufficient.

### Why `AllowedToAccept(Thing)` is the primary admission gate

`TryFindBestBetterStoreCellForWorker` (`StoreUtility.cs:215`) calls `slotGroup.Settings.AllowedToAccept(t)` **once per group**, then scans cells (`IsGoodStoreCell` → `NoStorageBlockersIn`) per candidate cell. So `AllowedToAccept(Thing)` is stockpile-scoped and fires before the per-cell loop — the right place for whole-stockpile counts, hysteresis, batch, and feeder. `NoStorageBlockersIn` is per-cell (wrong granularity for stockpile-wide limits) and fires more often.

> `StorageSettings.owner` (`IStoreSettingsParent`, `cs:10`) + private `SlotGroupParentOwner => owner as ISlotGroupParent` give the slot group from the settings instance. Two overloads exist: `AllowedToAccept(Thing)` (`:97`, hauling path — PSC enforces here) and `AllowedToAccept(ThingDef)` (`:114`, scan/UI path — leave vanilla so the def isn't globally hidden).

---

## 3. What "priority" actually is, in code

Answering the design question directly, because subpriority depends on it.

- **`StoragePriority`** is a `byte` enum with 6 values: `Unstored=0, Low=1, Normal=2, Preferred=3, Important=4, Critical=5` (`RimWorld/StoragePriority.cs`). Only `Low`–`Critical` are user-selectable (5 bands). `Unstored` is the "not in storage / move it out" sentinel.
- **`StorageSettings.Priority`** is a property backed by the scribed `priorityInt` field (`StorageSettings.cs:23,78`). That single byte is the entire stored priority.
- **The haul list** `HaulDestinationManager.AllGroupsListInPriorityOrder` is the slot groups sorted **descending by that enum** via `CompareSlotGroupPrioritiesDescending` → `((int)b.Priority).CompareTo((int)a.Priority)` (`HaulDestinationManager.cs:153`). It is re-sorted **only** on `Notify_HaulDestinationChangedPriority` (`:138`), i.e. when a priority changes — never per tick or per haul.
- **The search** `TryFindBestBetterStoreCellFor` (`StoreUtility.cs:173`) walks that list top→down and **breaks** the moment a group's band drops below the best band found so far. Within the top band that has any valid cell, `TryFindBestBetterStoreCellForWorker` (`:215`) keeps the **closest cell** (`closestDistSquared`). **Distance is the only within-band tiebreak.**

So, in one sentence: *priority is a single byte per slot group that (a) sorts the group list and (b) confines the haul search to the single highest band containing a reachable, accepting cell, after which proximity decides.*

**Consequence for subpriority:** vanilla offers no ordering finer than those 5 bands. Anything finer (a-z subpriority **and** the 1–10 half-steps) must be a PSC-owned *fine-order* layer that breaks ties the engine currently breaks by distance.

---

## 4. State & persistence architecture

Two scopes — keep them separate.

### 4.1 Limit/policy/fine-order state → attached to `StorageSettings`

```
static Dictionary<StorageSettings, PSC_StorageData> Map   // rebuilt each load
PSC_StorageData : IExposable                               // scribed by ExposeData postfix
```

- Scribed via a **postfix on `StorageSettings.ExposeData`** as a child node (e.g. `<psc>`).
- **Removal-safe (D1):** vanilla ignores the unknown node on load.
- **Add-safe:** no node ⇒ no dict entry ⇒ pure vanilla behavior.
- **Free vanilla-link sharing (D7):** `StorageGroup` is itself an `IStoreSettingsParent` holding one private `StorageSettings` (`StorageGroup.cs:6,15`). Linked members defer to it, so keying on `StorageSettings` shares limits **and** fine-order automatically across a linked group — which is the correct semantics (a linked group is one ordering unit).

Holds: per-def limits, batch size, the fine-order value (vanilla band is still the vanilla enum; fine-order is the sub-band tier + a-z letter).

### 4.2 Feeder links → `PSC_MapComponent`

Feeder source/destination links bind **haul units** (§6.3), which a shared `StorageSettings` can't express, so they live in a map component:

- Store each link as a pair of **stable string handles** (`GetUniqueLoadID()` of each endpoint), not `Scribe_References`.
- Resolve lazily on load; an unresolved endpoint (removed storage/mod) silently drops that link → removal- and add-safe, no warning spam, no polymorphic-collection unload hazard.

### 4.3 Explicitly avoided

No `Zone_Stockpile` subclass; no `Scribe_References` to storage endpoints; no static fields for runtime state beyond rebuildable dictionaries.

---

## 5. Performance strategy (top priority)

Naive "count of def X in this whole group" is O(cells × things) per admission check, on the haul hot path. The fix every mature reference uses (Variety Matters, LWM): **cache + invalidate on notify**, making the check O(1).

### 5.1 Core cache

```
PSC_StorageData {
    Dictionary<ThingDef,int> counts;     // live item count per def in this unit
    HashSet<ThingDef> refilling;          // hysteresis state per def (runtime, recomputable)
    ... limits, batch, fineOrder ...
}
```

- **Incremental** updates in the `Notify_ReceivedThing` / `Notify_LostThing` postfixes. Both `Building_Storage` (`:71,79`) and `Zone_Stockpile` (`:139,147`) implement these (the `ISlotGroupParent` contract, `ISlotGroupParent.cs:18,20`); the call sites are `Thing.SpawnSetup` (`Thing.cs:901`) and `Thing.DeSpawn` (`:1028`). (+ `AddCell`/`RemoveCell` for the unit growing/shrinking.)
- **Verified coverage / known gap:** because the hooks fire on spawn/despawn, all *whole-thing* removals are caught — hauled-out, rotted, burned, eaten-by-creating-a-new-thing, deconstructed. **Not caught:** in-place `stackCount` changes where the Thing stays spawned — a `Thing.SplitOff` partial-take (50→40 in the cell) or stack absorption. The cache drifts on those until corrected. This matters for the lower-limit/hysteresis (a stockpile partially consumed in place could otherwise never see itself drop below the refill threshold). ⇒ **Option D periodic resync is a baseline, not optional** (or additionally patch `SplitOff`/absorption — see §5.3).
- Admission postfix = one dict lookup + compares. No scan, no allocation, no LINQ.
- `refilling[def]` flips on at `count ≤ lower`, off at `count ≥ upper`; recomputable ⇒ not scribed (self-heals to safe default on load).

### 5.2 The single most important rule: opt-in early-out

> **A stockpile with no PSC settings pays effectively nothing.** Every postfix's first line is a lookup; no `PSC_StorageData` ⇒ return immediately. The entry is created on first setting, removed when the last clears. 99% of a colony's stockpiles stay vanilla-fast.

### 5.3 Options

| Option | Mechanism | Verdict |
|---|---|---|
| **A — Eager incremental count cache** | per-unit per-def counts via Notify; O(1) checks | **Recommended** (matches Variety Matters/LWM). Checks are frequent during hauling ⇒ eager beats lazy. |
| B — Lazy + dirty flag | recompute on next check when dirty, cache for the tick | Fallback if A's notify volume ever bites. |
| C — Per-cell only | per-tile caps, no aggregation | Wrong semantics (PSC is unit-wide). Reject. |
| D — Staggered resync | recompute exact counts for tracked units on throttled `MapComponentTick` (/250) | **Baseline alongside A** — corrects in-place stackCount drift (§5.1). Alternative/supplement: patch `Thing.SplitOff` + absorption to adjust the cache directly. |

### 5.4 Cross-cutting rules

- No per-check allocations; reuse + `Clear()`.
- Capacity via `cell.GetMaxItemsAllowedInCell(map)` / `GetItemStackSpaceLeftFor` (LWM/Ogre-aware, satisfies "see actual sizes", D13).
- Fine-order postfix guarded behind a per-map "any fine-order in use?" bool (§6.4) ⇒ one branch when unused.
- **Reservation overshoot (accepted v1):** without counting in-flight reservations, several pawns can briefly overshoot the upper limit, then it stops accepting and drains. Variety Matters adds reservation counting — defer unless testing shows it matters.

---

## 6. Per-feature notes

### 6.1 Limits + UI
Track in **items**; stacks are display only (`B = ceil(A/stackLimit)`, `Y = ceil(X/stackLimit)`), `stackLimit` read live (D13). Control panel is a **separate `Window`** opened by the PSC side button (depth-scaling: one button, rest behind it). Per-row state (`A (B)-X (Y)` label, I-beam, right-click, green/red buttons) via `Listing_TreeThingFilter.DoThingDef`/`DoCategory`, mirroring MFH.

### 6.2 Batch (D12, cheap gate)
In the §7 admission postfix: require `room ≥ batch` **and** `t.stackCount ≥ batch`. Rejects tiny single-stack hauls (the case that matters). Known imperfection: vanilla's `haulOpportunisticDuplicates` can aggregate same-def stacks en route, which admission can't see — occasional batches slightly under target when only fragmented stacks exist. Accepted; the exact WorkGiver-sum alternative was rejected on cost.

### 6.3 Feeder links — member vs group

This is the concept I under-explained. Vanilla "link storage":

- A **member** is one physical storage object — a shelf, or a stockpile zone — implementing `IStorageGroupMember` with a `StorageGroup Group { get; set; }` (`RimWorld/IStorageGroupMember.cs:5,7`). Unlinked ⇒ `Group == null`.
- A **`StorageGroup`** is the object several members are joined into ("link storage" gizmo). It owns the **one shared `StorageSettings`** and presents a **combined `CellsList`** (union of all members' cells). Hauling treats the group as a single unit: `obj = slotGroup.StorageGroup ?? slotGroup` then iterates `obj.CellsList` (`HaulAIUtility.cs:141`).

So when 3 shelves are linked, hauling sees **one** unit of 3 shelves' worth of cells, sharing one filter/priority. An individual shelf is a *member* of that unit.

**Feeder endpoint = the haul unit (D7):** `member.Group` when linked, else the standalone slot group / zone. This matches how hauling already reasons, and means a feeder link to "those 3 linked shelves" is one link to their group, not three. Stored by the group's load-ID string (§4.2). If a member is later unlinked or the group dissolves, the handle fails to resolve and the link drops cleanly.

Both toggles enforced in the §7 postfix (cheap: one `GetSlotGroup(t)` + set membership). Overlay arrows mirror Contagion's `GenDraw` line/arrow approach, MapComponent-toggled.

### 6.4 Fine-order (subpriority + 1–10) — postfix design, no transpiler (D14)

Both features need one ordering layer below the vanilla band (§3). Model it as a single **fine-order key** per unit: `(vanilla band, 1–10 sub-tier, a-z letter)`, stored on `PSC_StorageData`. The vanilla band stays the untouched enum; the sub-tier and letter are PSC's.

Realized with two postfixes, both using **public** APIs:

1. **Postfix `HaulDestinationManager.CompareSlotGroupPrioritiesDescending`** (`:153`): when the two groups share a vanilla band, break the tie by fine-order. This runs only inside the insertion-sort on priority change (`Notify_HaulDestinationChangedPriority`) — **not** on the haul hot path — so it's nearly free and keeps `AllGroupsListInPriorityOrder` fully ordered.

2. **Postfix `StoreUtility.TryFindBestBetterStoreCellFor`** (`:173`): when `__result == true` and fine-order is active on the map, re-select within the chosen band. Walk `AllGroupsListInPriorityOrder` for groups in that band (they're contiguous and, thanks to postfix #1, fine-order sorted); take the first/best fine-order group(s) that `AllowedToAccept(t)` and have an `IsGoodStoreCell` (public, `StoreUtility.cs:327`); pick the closest good cell among them; override `out foundCell` if different.

Why this works: vanilla already narrowed to the single best band; we only re-rank *within* that band, replacing distance-only with fine-order-then-distance. No private members, no IL — `AllowedToAccept`, `IsGoodStoreCell`, and `AllGroupsListInPriorityOrder` are all public.

Cost: when active, the outer postfix re-scans the top band's groups (bounded by their count) for the item being placed. Guarded by the per-map "any fine-order in use?" flag ⇒ zero cost otherwise. Risk: **Medium** (postfix duplicates some selection work; no fragility from IL). If profiling shows the re-scan is hot, postfix #1's ordering lets us short-circuit to the first valid group cheaply.

### 6.5 1–10 priority (corrected: distinct when active)

10-mode is **not** cosmetic — it adds real ordering through the §6.4 fine-order layer (two sub-tiers per vanilla band):

| # | Vanilla band | Fine sub-tier |
|---|---|---|
| 1 | Critical | high |
| 2 | Critical | low |
| 3 | Important | high |
| 4 | Important | low |
| 5 | Preferred | high |
| 6 | Preferred | low |
| 7 | Normal | high |
| 8 | Normal | low |
| 9 | Low | high |
| 10 | Low | low |

- **Distinct when active:** "1" and "2" share the Critical *enum* but differ by sub-tier, so the §6.4 postfix orders 1 before 2. Not identical.
- **Collapse** (setting off / mod removed): drop the sub-tier ⇒ pair merges to its band — `1,2→Critical · 3,4→Important · 5,6→Preferred · 7,8→Normal · 9,10→Low`. Matches the design table. Safe because the vanilla enum was never changed.
- a-z subpriority stacks below the sub-tier: full key `(band, sub-tier, letter)`.
- Reverse-order remains a UI label flip only.

> Display anchors **confirmed**: Critical→1, Important→3, Preferred→5, Normal→7, **Low→10**. The top four bands anchor to their high sub-tier; Low anchors to its low sub-tier so the extremes read as 1 (best) and 10 (worst). Intentional asymmetry; no ordering impact (the fine-order still sorts 1<2<…<10). On enabling 10-mode, existing vanilla stockpiles land at {1,3,5,7,10}; {2,4,6,8,9} are the newly-reachable finer levels.

---

## 7. Admission decision (single postfix, all rules)

`AllowedToAccept(Thing t)` postfix, **tighten-only** (never overrides a vanilla `false`):

```
if vanilla false: return
data = lookup(settings); if data == null or no rule for t.def: return
unit = settings.owner.GetSlotGroup();  n = data.counts[t.def]      // O(1)

if upper set and n >= upper:                       result = false; return   // D2
if not data.refilling[t.def]:                      result = false; return   // lower/hysteresis
if batch set and (upper - n) < batch:              result = false; return   // D12 room
if batch set and t.stackCount  < batch:            result = false; return   // D12 source stack
src = StoreUtility.GetSlotGroup(t)                                          // null = loose
if data.onlyFromSource and src not in data.sources:           result = false; return   // D11
if src has onlyToDestinations and unit not in src.destinations: result = false; return
```

`HaulToCellStorageJob` postfix clamps `__result.count = min(count, upper - n)`.

---

## 8. Mod integration

| Mod | Approach | Status |
|---|---|---|
| **LWM / Ogre / multi-stack** | `GetMaxItemsAllowedInCell` / `GetItemStackSpaceLeftFor`; count by items; probe `IHoldMultipleThings`; never assume 1 stack/cell | core (D13) |
| **Pick Up And Haul** | postfix `WorkGiver_HaulToInventory.CapacityAt` (Stack Gap pattern), soft-dep guarded; avoid transpiling PUAH lambdas | v1 |
| **Flickable Storage** | MIT → integrate on/off + receive-only directly; note interaction with feeder toggles | D9 |
| **Material Filter (Harmony)** — in-house | share `DoThingDef` row space; order `Priority·Letter·MFH·X`; filters compose (material AND count) | D10 |
| **Direct placement (bill output)** | may momentarily exceed cap, then drain (acceptable, D2); Stack Gap's `GenPlace.TryPlaceDirect` if hard caps ever wanted | deferred |
| **Other limit mods** | migration deferred | D8 |

---

## 9. Risk register

| Risk | Severity | Mitigation |
|---|---|---|
| Fine-order outer postfix re-scans top band when active | **Medium** | opt-in guard; postfix #1 ordering enables early short-circuit; profile |
| Batch undershoot from opportunistic-duplicate aggregation | Medium | accept stack-size gate (D12); WorkGiver gate later if needed |
| Per-row UI in `Listing_TreeThingFilter` | Medium | proven by MFH + Storage Sorting + Stack Gap; reuse MFH |
| Reservation overshoot past upper limit | Low | self-corrects via drain; add reservation counting later |
| Count-cache drift from in-place `stackCount` change (`SplitOff` partial-take, absorption) — not just non-haul entry | **Med** | Option D resync as baseline (§5.1/5.3), or patch `SplitOff`/absorption; affects lower-limit/hysteresis if unaddressed |
| Priority button width hardcoded (`160f`, `FillTab`) | Low | place letter box beside it rather than shrinking it (avoids touching `FillTab` layout) |

---

## 10. Open questions

1. ~~Batch approximation~~ — **resolved (D12):** cheap gate.
2. ~~Feeder endpoints~~ — **resolved (D7):** the haul unit (`StorageGroup` if linked, else standalone group).
3. ~~What is priority~~ — **resolved (§3).**
4. ~~Subpriority via transpiler?~~ — **resolved (D14):** postfixes only.
5. ~~1–10 display anchors~~ — **resolved:** Low→10 (extremes 1=best, 10=worst); §6.5.
6. ~~Priority button shrink~~ — **resolved:** place letter box beside the 160px button for now; shrinking is optional later UI tuning, not M1.

---

## 11. Milestone slicing

| Milestone | Contents | Rationale |
|---|---|---|
| **M1 — Core limits** | `StorageSettings` state + ExposeData/clipboard + count cache + Notify hooks + `AllowedToAccept` upper/lower/hysteresis + `HaulToCellStorageJob` clamp + side-button window + per-row UI | Load-bearing 80%; everything builds on cache + admission |
| **M2 — Batch + PUAH + multi-stack** | batch gate, `CapacityAt` PUAH clamp, LWM/Ogre capacity correctness | hauling polish on M1 |
| **M3 — Feeder links** | MapComponent link store, feeder rules in admission, Contagion-style overlay, auto-priority | independent of limits; parallel after M1 |
| **M4 — Fine-order (subpriority + 1–10)** | two ordering postfixes + relabel UI + opt-in guard | postfix-based now (D14), lower risk than first thought; still last so the rest is stable |
| **M5 — Migration / Flickable** | import other mods' limits; Flickable behaviors | deferred (D8/D9) |

---

*Source verification: vanilla signatures confirmed against `Rimworld 1.6.4850 Decompiled Source` via the `rimworld-source` MCP. Reference-mod seams from the `Stockpile Manipulation` reference group (`SatisfiedStorage`, `Stockpile Limit`, `Variety Matters Stockpile`, `Stack Gap`, `Storage Sorting`, `LWM Deep Storage`).*
