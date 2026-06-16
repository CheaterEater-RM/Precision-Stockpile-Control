# Precision Stockpile Control - Milestones

## Completed

### M0 - Scaffold

- Created RimWorld mod directory structure: `About/`, `1.6/Assemblies/`, `Source/`, `Source/Patches/`, `Languages/English/Keyed/`
- Materialized template files with PSC identity values
- Left source implementation files out of the scaffold

## In Progress

### M1 - Core Limits (code complete; pending in-game verification)

Implemented (builds clean, `net48`, Harmony 2.4, 0 warnings):

- **State + persistence.** `PscStorageData : IExposable` keyed by `StorageSettings` in
  `PscStorageDataStore` (rebuildable static map, cleared by `PscGameComponent` on game load).
  Scribed as a `<psc>` child node via a `StorageSettings.ExposeData` postfix — written only when
  policy is non-default, so add/remove are save-safe.
- **Copy/paste + link-init.** Single `StorageSettings.CopyFrom` postfix deep-copies PSC policy
  (covers clipboard Copy/PasteInto and `StorageGroup.InitFrom`). Refinement over the design's
  Copy/PasteInto pair — all three route through `CopyFrom`.
- **Haul-unit resolver.** `PscHaulUnit` wraps the canonical `ISlotGroup` (`StorageGroup` when
  linked, else the slot group); `ResolveSettings` / `ResolveCurrent`. Single source of truth.
- **Count cache.** Dirty-mark + lazy recompute-from-`HeldThings` (refinement over +/- deltas, to
  eliminate drift bugs). Drift seams patched: `Building_Storage`/`Zone_Stockpile`
  `Notify_Received/LostThing`, `Thing.SplitOff`, `Thing.TryAbsorbStack`, `Zone_Stockpile`
  `AddCell`/`RemoveCell`, `StorageGroupUtility.SetStorageGroup` (link/unlink). Staggered resync
  backstop in `PscMapComponent.MapComponentTick`.
- **Demand index + per-map runtime.** `PscMapComponent` (auto-instantiated) holds `anyPscActive`,
  the D17 demand index (policy-level granularity for M1), tracked-settings set, and resync.
- **Admission + clamp.** Tighten-only postfix on `AllowedToAccept(Thing)` (upper target-max +
  lower/hysteresis, D15) with a **source==target guard** so a capped stockpile never flags its own
  contents as haulable (generalized D16 — caught during review of `IsInValidBestStorage`). Upper
  clamp postfix on `HaulAIUtility.HaulToCellStorageJob`.
- **UI.** PSC side button (`ITab_Storage.FillTab` postfix) → `PscControlWindow` (items/stacks
  toggle, target-max + refill sliders, batch input as groundwork, apply/remove-to-search bound to
  the vanilla search box). Read-only compact per-row limit label (`Listing_TreeThingFilter.DoThingDef`
  postfix). UI says **"target maximum"** (soft cap, D2) until M2.
- **Groundwork for M2-M5** baked in: `PscStorageData` carries batch/feeder/fine-order fields
  (write-guarded); admission + clamp postfixes have commented M2/M3 insertion points; demand index
  and `PscHaulUnit.UniqueLoadID` ready for M3.

Deferred to a UI follow-up (per scope decision): I-beam mixed-state symbol, right-click limit
submenu, click-drag limit propagation, category propagation.

Remaining before M1 is "done": in-game verification of the design §14 M1 scenarios (load/save,
removal-safety, hysteresis, split/absorb/link-unlink count accuracy, no-slowdown early-out).

### M2 - Hard Caps, Batch, and Integration (code complete; pending in-game verification)

Scope chosen: **focused hard cap** (see `04_PSC_DESIGN.md` §8.1). Builds clean, 0 warnings.

- **Focused hard cap.** Per-unit live-count cap at the carry-drop seam: a cancelling prefix on
  `Pawn_CarryTracker.TryDropCarriedThing` (no-count overload) drops only `upper − liveCount` into a
  capped unit and leaves the remainder for vanilla's place-fail fallback. Shared room calculator
  `PscCap.TryGetRoom(cell, map, def)` + new `PscHaulUnit.ResolveCell`. Chosen over the
  `PlaceHauledThingInCell`-lambda transpiler (Stockpile Limit precedent) for robustness — no
  compiler-lambda reflection, and it also catches manual/drafted drops. Planning (`AllowedToAccept`)
  and drop enforcement share the M1 count cache, so there is no re-haul loop.
- **Batch (D12).** Source-stack gate in `AllowedToAccept` (`t.stackCount < batch ⇒ reject`) + final
  count cancel in `HaulToCellStorageJob` (`job.count < batch ⇒ null`). Window now reads/writes
  `PscStorageData.batch` live.
- **Pick Up And Haul.** Soft-dep postfix on `WorkGiver_HaulToInventory.CapacityAt`
  (`Prepare()`/reflection, no hard reference) reduces capacity to unit room. Best-effort multi-cell.
- **Multi-stack (LWM/Ogre).** Free — counts/caps are in items; PUAH queries `IHoldMultipleThings` first.
- **UI.** "target maximum" → "maximum"; `PSC_SoftCapNote` now states hauling respects the maximum
  with the rare direct-spawn caveat.

**Left soft (documented):** direct spawns into storage (`GenPlace.TryPlaceDirect` — a workbench
standing inside the stockpile, map-gen scatter, mod direct-spawns). Normal crafting is covered
(bill products are hauled from the bench). Closing this gap (a `GenPlace.TryPlaceDirect` per-unit
cap) is deferred and can be added without disturbing the focused seams.

New/changed files: `Source/Patches/HardCap_Patches.cs`, `Source/Patches/PickUpAndHaul_Patch.cs`,
`Source/Core/PscHaulUnit.cs` (+`ResolveCell`), `Source/Patches/Admission_Patches.cs` (batch gates),
`Source/UI/PscControlWindow.cs` + `Languages/.../Keys.xml` (relabel + live batch).

Remaining before M2 is "done": in-game verification of the design §14 M2 scenarios (drop over max
blocked, batch ≥ N, PUAH no overfill, LWM multi-stack, PUAH-absent soft-dep load).

### UI follow-up (deferred depth-scaling per-row UI; code complete, pending in-game verification)

Built after M2, per the sequencing decision. Builds clean.

- **Shared limit editor.** `PscLimitEditor` (items/stacks + max/refill controls) extracted from the
  control window and reused by the per-item submenu, plus a `PscEdit` helper that mutates one def's
  policy and keeps the vanilla filter in sync. `PscControlWindow` refactored onto it.
- **Right-click submenu.** `PscItemLimitMenu` (Apply / Cancel / ✓ allow-clear / ✗ disallow) opens
  from a right-click on a filter row (single def) or a category (its storable descendants → category
  propagation). `PscUiContext` now carries the unit even with no data yet, so right-click can create
  a first limit.
- **Limit propagation drag.** `PscFilterPaint`: right-drag copies the start row's limit across rows
  the cursor passes (left button stays pure vanilla allow/disallow to avoid conflicts — a deliberate
  deviation from the design's left-drag, chosen for robustness).
- **Category I-beam / shared label.** `DoCategory` postfix draws a ⊤—⊥ I-beam glyph (GUI primitives,
  no texture) when a category's limited items disagree, or the shared limit text when they match.
- **Row-Y fix.** `DoThingDef`/`DoCategory` capture the row's Y in a prefix `__state` because vanilla
  `EndLine()` advances `curY` before the postfix runs (the M1 read-only label was off by one row).

New files: `Source/UI/PscLimitEditor.cs` (+`PscEdit`), `Source/UI/PscItemLimitMenu.cs`,
`Source/UI/PscFilterPaint.cs`, `Source/Patches/Listing_TreeThingFilter_DoCategory_Patch.cs`; edits to
the `DoThingDef` patch, `PscControlWindow`, `PscUiContext`, the `FillTab` patch, `Keys.xml`, and
`.csproj` (+`UnityEngine.InputLegacyModule`).

Deferred (not done): the design's left-click-opens-submenu-when-limited nicety (replaced by
right-click for robustness); per-category limit-state caching (recomputed each frame — fine for
typical limit counts).

## Planned

### M3 - Feeder Links

- Source/destination link persistence
- Feeder admission rules and overlay
- Auto-priority behavior and invalid-link feedback

Dependencies: M2 complete.

### M4 - Fine Order

- Fine-order search transpiler
- Priority-list tie-break postfix
- Subpriority and 1-10 priority UI
- Version-gated fail-closed behavior

Dependencies: M3 complete.

### M5 - Migration and Flickable Storage

- Migration from supported limit mods
- Integrated on/off and receive-only storage behavior

Dependencies: M4 complete.
