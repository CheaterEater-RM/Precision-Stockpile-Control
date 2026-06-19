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

Scope chosen: **focused hard cap** (see `docs/04_PSC_DESIGN.md` §8.1). Builds clean, 0 warnings.

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
- **Batch empty.** Mirror of batch fill, keyed on the **source** unit (`PscStorageData.batchEmpty`,
  save field `batchEmpty`): a source-keyed gate in `AllowedToAccept` (`t.stackCount < batchEmpty ⇒
  reject`, churn-safe via the existing `sourceIsTarget` guard) + a source-keyed cancel in
  `HaulToCellStorageJob` (`job.count < batchEmpty ⇒ null`). Control window shows **Batch fill** and
  **Batch empty** side by side. Verified against PUAH and Hauler's Dream: both route through
  `AllowedToAccept`, so the admission gate holds; both build their own bulk jobs, so the trip-size
  cancel is best-effort there (admission gate is the cross-mod line). No new Harmony patches.
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

Polish update: PSC-limited I-beam markers now own left-click and left-drag in the vanilla filter
checkbox slot. Left-click opens the limit menu, left-drag propagates the starting limit as stack
ratios across rows, untouched rows keep vanilla allow/disallow paint-drag, and right-click/right-drag
remain compatibility paths.

- **Shared limit editor.** `PscLimitEditor` (items/stacks + dual-handle nullable lower/upper
  slider, direct entry boxes, stack-aware ticks, mixed-stack items-mode disable notice) is reused by
  the control window and per-item submenu. `PscEdit` mutates one def's policy and keeps the vanilla
  filter in sync.
- **Per-row submenu.** `PscItemLimitMenu` opens from a left-click on a PSC-limited row's I-beam or
  category marker; right-click remains a compatibility path and can still create a first limit.
- **Limit propagation drag.** `PscFilterPaint`: left-drag from a PSC-limited marker copies the start
  row's limit across rows the cursor passes, converting item counts through source/target stack
  ratios so mixed stack sizes keep the same stack fraction. Untouched rows keep vanilla
  allow/disallow paint-drag; right-drag remains a PSC compatibility path.
- **Storage tab polish.** `ThingFilterUI` is shifted down while PSC context is active, reserving a
  row for the PSC button under Priority and above Clear all / Allow all. The global control window
  closes when its original storage is deselected or replaced by a different selection. Vanilla
  Clear all / Allow all clear per-def PSC limits while preserving stockpile-wide PSC policy.
- **Shelf / multi-stack capacity.** The limit editor sizes stack-mode maxima from live per-cell stack
  capacity (`GetMaxItemsAllowedInCell`) instead of cell count, with a held-stack fallback.
- **Category I-beam / shared label.** `DoCategory` postfix evaluates currently allowed storable
  descendants only. It draws a yellow I-beam texture/fallback when allowed children have mixed
  limits, or the shared limit text when all allowed children share the same non-null range. Item
  rows use the same inline item/stack format as the main editor when stack context is available.
- **Row-Y fix.** `DoThingDef`/`DoCategory` capture the row's Y in a prefix `__state` because vanilla
  `EndLine()` advances `curY` before the postfix runs (the M1 read-only label was off by one row).

New files: `Source/UI/PscLimitEditor.cs` (+`PscEdit`), `Source/UI/PscItemLimitMenu.cs`,
`Source/UI/PscFilterPaint.cs`, `Source/UI/PscUiWidgets.cs`,
`Source/Patches/Listing_TreeThingFilter_DoCategory_Patch.cs`, `Source/Patches/ThingFilterUI_Patch.cs`,
`Source/Patches/ThingFilter_AllowAll_Patches.cs`;
edits to the `DoThingDef` patch, `PscControlWindow`, `PscUiContext`, the `FillTab` patch, `Keys.xml`,
and `.csproj` (+`UnityEngine.InputLegacyModule`).

Deferred (not done): per-category limit-state caching (recomputed each frame — fine for typical
limit counts).

### M3 - Feeder Links (code complete; pending in-game verification)

Builds clean, `net48`, Harmony 2.4, 0 warnings.

- **Link store.** Authoritative directed-edge list (`PscFeederLinks` / `PscFeederLink`) owned by
  `PscMapComponent`, scribed via a new `ExposeData` (`<feederLinks>`, written only when non-empty so
  add/remove stay save-safe). Endpoints are `PscHaulUnit.UniqueLoadID` strings; unresolved endpoints
  drop silently and are pruned on load and endpoint lifecycle changes. Derived runtime indices (edge
  set + source/dest adjacency) rebuilt lazily behind a dirty flag.
- **Admission rules.** Feeder block added to the `AllowedToAccept(Thing)` postfix, evaluated *before*
  the target-data early-out (a source's `onlyToDestinations` must block hauling even into a target
  with no PSC policy) and gated behind a per-map `anyFeederActive` flag. Both rules — target
  `onlyFromSource` and source `onlyToDestinations` — reduce to the same functional directed edge:
  `HasEdge(source, target)` and destination priority > source priority. Loose items have no source
  edge, so `onlyFromSource` rejects them. D16 source==target guard reused.
- **Haul context.** Feeder `HaulToCell` jobs register runtime-only source/destination ids for the
  hauled thing; the context transfers to split carried stacks and is cleared on storage receipt or
  stale route pruning. This keeps carried-item revalidation from losing the planned feeder source.
  Feeder jobs disable opportunistic duplicate pickup for M3.
- **Gizmos.** Six feeder gizmos (`PscFeederGizmos`) on zones and shelves via `Zone_Stockpile`/
  `Building_Storage` `GetGizmos` postfixes (single-selection gated): Connect source, Connect
  destination (paint `Designator_PscFeederLink` — click links one storage, drag paints every storage
  the cursor passes over), Only-from-source / Only-to-destinations toggles (grayed until a source/destination
  exists; seed strictness from mod-setting defaults on the first link), Show connections (overlay
  toggle), Clear all connections (right-click float-menu required). A pair carries at most **one**
  direction: `AddFeederLink` drops any existing reverse edge first, so linking each of two piles to the
  other flips the direction (and clears flags the old direction orphaned) instead of forming an A↔B 2-cycle.
- **Overlay.** `PscFeederOverlay` (Contagion pattern) drawn from `MapComponentUpdate`: green arrowed
  lines for valid links (destination outranks source, D5), red + × for non-functional. Draws the
  selected storage's links, or every link when "Show connections" is on.
- **Copy/paste + vanilla link.** Link endpoints carry on copy-paste via
  `StorageSettingsClipboard.Copy`/`PasteInto` postfixes (session-static payload, "replace"/adopt
  semantics — the pasted-onto unit adopts the copied unit's source/dest lists). Vanilla link/unlink
  handled in a `StorageGroupUtility.SetStorageGroup` postfix (`AdoptLinks` reciprocal duplication).
  Runtime pruning covers zone deletion, storage despawn/destroy, dead storage-group ids, and
  cross-map paste endpoints. When a unit loses its last incoming/outgoing link, the matching strict
  feeder flag auto-clears so the gizmo cannot strand a stockpile in an unusable state.
- **Settings.** `defaultOnlyFromSource` / `defaultOnlyToDestinations` (both default on) added to
  `PscSettings` + settings window. `autosetSourcePriority` left persisted but **no-op** — auto-priority
  deferred to M4 (it needs the fine-order letter mechanism); M3 enforces link validity only.

New files: `Source/Core/PscFeederLinks.cs`, `Source/UI/PscFeederOverlay.cs`,
`Source/UI/PscFeederGizmos.cs`, `Source/UI/Designator_PscFeederLink.cs`,
`Source/Patches/Feeder_Gizmos_Patch.cs`, `Source/Patches/Feeder_Lifecycle_Patches.cs`.
Edits: `PscMapComponent` (link store + ExposeData + overlay hook + mutators + prune),
`PscHaulUnit` (+`TryGetDrawCenter`, `FromSlotGroup`), `Admission_Patches` (feeder slot),
`PscMod`/`Keys.xml`.

Remaining before M3 is "done": in-game verification of the design §14 M3 scenarios (feeder
own-contents validity, loose-item rejection, opportunistic duplicates, red invalid links, copy-paste
and vanilla link/unlink carry, save-compat). Known TODO: gizmo icons reuse vanilla command textures
(swap in custom art later).

### M4 - Fine Order (code complete; pending in-game verification)

Builds clean, `net48`, Harmony 2.4, 0 warnings. Scope this slice: **a-z subpriority + 1-10
numbering**, on the **Full** mechanism (sort baseline + transpiler). Auto-priority (D4) shipped as a
follow-up slice (below); the dev-mode self-test/debug overlay from the design's M4 slice is **deferred**.

- **Fine-order engine.** `PscOrder` (new) owns the key and comparison. Ordering is a "rank within
  band" (lower = better) composed of effective sub-tier then letter. The vanilla `StoragePriority`
  enum is never touched (D6): sub-tier `0` = unset = band anchor; 1-10 numbering off collapses sub-tier
  to tier 1 so only letters refine, and mod removal leaves both fields inert. Includes the 1-10
  mapping (anchors `{1,3,5,7,10}`; Low anchors at tier 2) and the reverse-order **label-only** flip.
- **Sort tiebreak (baseline).** Postfix on `HaulDestinationManager.CompareSlotGroupPrioritiesDescending`
  breaks same-band ties by fine-order so newly-hauled / unstored items prefer the finer-ranked group.
  Not a transpiler — never IL-conflicts; this is the conflict-proof floor.
- **Fail-closed transpiler.** Narrow `CodeMatcher`-style transpiler on
  `StoreUtility.TryFindBestBetterStoreCellFor` injects a guard before the `priority <= currentPriority`
  break (mirrors LWM's proven minimal `ble`-only edit) so an already-stored item can relocate to a
  strictly-better same-band group. If the 1.6.4850 IL fingerprint (`ldloc priority; ldarg.3; ble`)
  isn't found, it logs **one** error, sets `PscOrder.TranspilerFailed`, yields original IL, and
  disables relocation only (sort baseline still works).
- **LWM Deep Storage composition.** LWM's transpiler on this method is dormant (`[HarmonyPatch]`
  commented out, applied via `PatchAll`); its live capacity enforcement is on `IsGoodStoreCell` /
  `NoStorageBlockersIn`. PSC only changes which groups the search considers — every candidate cell
  still passes `IsGoodStoreCell`, so PSC never overfills a DSU. Stockpile Limit / Variety Matters
  patch the same method as **prefixes**, which compose fine.
- **Per-map gate.** `PscMapComponent.anyFineOrderActive` (a unit has `subTier != 0` or a letter) gates
  the transpiler helper; `NotifyOrderChanged` updates tracking and calls
  `Notify_HaulDestinationChangedPriority` so the sorted list rebuilds on an edit.
- **Feeders unified onto the key (D5).** `HasFunctionalFeederEdge` now requires the destination to
  strictly outrank the source by full key (`PscOrder.Outranks`) instead of vanilla band only, so
  same-band feeders work with 1-10 / letters. With no fine-order set, behavior is identical to M3.
- **UI.** `PscPriorityBox` (new) draws beside the vanilla Priority button (tab is 300 wide; button is
  160, leaving room): an always-on **letter box** (a-z menu) and, when 1-10 numbering is on, a **level
  box** (1-10 menu that sets band + sub-tier). The vanilla button is left intact (D6/§10.7).
- **Settings.** `priorityNumbering` + `reverseOrder` added to `PscSettings` (+ "Fine order" header in
  the settings window). Toggling 1-10 re-sorts every map. `autosetSourcePriority` wired in the
  auto-priority follow-up (below); `linkSubpriorities` unused this slice (a `StorageGroup` shares
  one key already).

New files: `Source/Core/PscOrder.cs`, `Source/Patches/FineOrder_Patches.cs`,
`Source/UI/PscPriorityBox.cs`. Edits: `PscMapComponent` (anyFineOrderActive + NotifyOrderChanged +
fine-order feeder edge), `PscMod` (settings + UI), `ITab_Storage_FillTab_Patch` (draw priority box),
`Keys.xml`.

Remaining before M4 is "done": in-game verification of the design §14 M4 scenarios (same-band
relocation selected/not-selected, Low-band floor, transpiler-mismatch fail-closed), 1-10 collapse,
unified same-band feeders, no equal-key churn, LWM capacity respected.

### M4 follow-up - Auto-priority (code complete; pending in-game verification)

Builds clean, `net48`, Harmony 2.4, 0 warnings. Wires the previously-inert `autosetSourcePriority`
(D4) — plus a sibling `autosetDestinationPriority` — to nudge priorities when a feeder link is
painted, so the link is functional immediately. The two directions are **independent opt-in toggles**.

- **Symmetric letter step, anchored on the selected pile.** On a freshly created link, the **painted**
  unit is stepped one fine-order **letter** onto the correct side of the **selected** (anchor) unit:
  Connect-source steps the painted source DOWN one letter below the anchor destination (e.g. anchor `5`
  → source `5a`, gated on `autosetSourcePriority`); Connect-destination steps the painted destination UP
  one letter above the anchor source (gated on the separate `autosetDestinationPriority`). Stays within
  the anchor's band + sub-tier (works whether or not 1-10 numbering is on).
- **Guarded + clamped.** No-op when the destination already strictly outranks the source (never raises a
  source or lowers a destination that already satisfies the requirement). Clamps at the band's letter
  range (`z` at the bottom, no-letter at the top); when a strict step is impossible it makes **no
  change** and posts a player message (`PSC_AutoPriorityClampLow`/`High`), deduped during drag-paint.
- **Seams.** Logic lives in `PscOrder.PlaceSourceBelowDest` / `PlaceDestAboveSource` (+ `AutoOrderResult`,
  letter-step helpers, `ApplyOrder` reusing the manual-box write path → `NotifyOrderChanged`).
  `PscFeederManager.AddFeederLink` now returns whether a new edge was created; the link designator
  (`Designator_PscFeederLink.AutoPriority`) runs it once per new edge, only when that direction's
  setting is on. Excludes copy/paste (it replicates exact setups). Both toggles default **off**.

Edits: `PscOrder`, `PscFeederManager`, `Designator_PscFeederLink`, `PscMod` (two checkboxes:
`autosetSourcePriority` + `autosetDestinationPriority`), `Keys.xml`. New persisted setting field
`autosetDestinationPriority` (additive); no change to existing save fields (`subTier`/`letter` already persist).

Remaining before "done": in-game verification of the design §14 scenarios (down/up step under
numbering on and off, the already-satisfied guard, both clamp messages, each toggle independently
on/off — including one direction on while the other is off).

### M5 (part 1) - Migration from other limit mods (code complete; pending in-game verification)

Builds clean, `net48`, Harmony 2.4, 0 warnings. One-way import (switch *to* PSC; never back). Full
design in `docs/04_PSC_DESIGN.md` §16.

- **Enabling feature — per-unit default limit.** `PscStorageData.defaultLimit` (a `PscDefLimit`, in
  items) applies to any allowed def with no explicit per-def entry; scribed as a `<default>` child of
  `<psc>` (written only when set). Admission / hard-cap / refill read paths now go through
  `HasEffectiveLimit` / `GetEffectiveLimit` (per-def wins, else default); `RecomputeAll` and
  `Notify_DefaultLimitSet` extend hysteresis to default-covered present defs. Editable as the
  "Default limit (all items)" row in the control window. Useful on its own ("cap this whole pile").
- **Mechanism.** The three supported mods persist inside the vanilla `<settings>` node (same seam as
  `<psc>`). On load, with the old mod removed, PSC's own `StorageSettings.ExposeData` postfix reads the
  orphaned sibling nodes from `Scribe.loader.curXmlParent` — no reflection, no save scan, no loadID
  matching. Two phases: **capture** (`PscMigration.TryCaptureForeign`, during `LoadingVars`; a present
  `<psc>` always wins) → **resolve** (`PscMigration.ResolveAllPending`, from
  `PscGameComponent.FinalizeInit`, after maps are ready: convert, register, seed refill, notify).
- **Supported mods (priority order, by usage):** Stack Gap (`Andromeda.StackGap`), Satisfied Storage
  (`ryder.SatisfiedStorage`), Variety Matters Stockpile (`Mlie.VarietyMattersStockpile`). **Dropped:**
  Stockpile Limit (old, low usage). **Not supported:** Storage Sorting (`Ghosty.SortingMod`) —
  zone-keyed MapComponent, not `<settings>`, and mostly acceptance filtering. Per-mod conversions +
  fidelity in §16.3 (Stack Gap `allowedPerItem` is exact; its `stackGapPercents` and the
  whole-stockpile sources are approximate; per-stack-size caps are dropped).

- **Iteration 2 (fix pass, from reading real test saves + decompiling `StackGap.dll`):** Stack Gap's
  `allowedPerItem` is serialized as **parallel `<keys>`/`<values>` lists** (not `<li><key>`), and the
  common usage is `stackGapPercents` — the original code parsed the wrong dict shape and skipped the
  percent range, so Stack Gap imported nothing; both are now handled (percent via Stack Gap's
  `round(maxStack × pct^3) × slots` curve, factor 3 assumed). Added verbose-debug diagnostics across
  capture/resolve (gate states, pending count, per-unit outcome) and a "found but mod still loaded"
  dev-log, since the absence of any logging made the original failure impossible to diagnose.
- **Iteration 3 (verified on real saves + mutual exclusivity):** confirmed migration end-to-end by
  reading 8 pre/post-PSC test saves — all three mods now write `<psc>` on resave with the foreign
  nodes gone (Stack Gap `WoodLog` per-def cap exact at `<upper>20>`, plus default refill/cap; VMS
  `duplicatesLimit`→default upper; Satisfied `fillPercent`→default lower). The earlier VMS "nothing
  migrated" was the still-loaded gate, as diagnosed. Also declared the four limit mods incompatible in
  `About.xml` (`Andromeda.StackGap`, `ryder.SatisfiedStorage`, both VMS forks
  `Cozarkian.VarietyMatters.Stockpile` / `Mlie.VarietyMattersStockpile`, `darkside.StockpileLimit`) —
  a soft mod-list warning that cooperates with the runtime gate (the real double-enforce guard), not a
  hard block. Storage Sorting, OgreStack, LWM, and Flickable stay compatible (different axis / integration
  targets). See §16.4.
- **Idempotency:** the first post-migration save writes `<psc>` and the foreign nodes vanish; the
  `<psc>` node is the "already migrated" marker (no explicit flag).
- **Gating:** each format is eligible only when its source mod is **absent** (`AccessTools.TypeByName`
  marker check, computed once), so a both-loaded setup never double-enforces.
- **Graceful failure (per Adrian):** per-unit conversion is wrapped; on error PSC drops that unit's
  data (plain allowed/unlimited), logs a warning, counts it failed. A one-time letter + always-on log
  line report imports and failures. Losing a limit is fine; corrupting storage is not.

New file: `Source/Core/PscMigration.cs`. Edits: `PscStorageData`, `Admission_Patches`,
`HardCap_Patches`, `StorageSettings_Persistence_Patch`, `PscGameComponent`, `PscControlWindow`,
`PscLimitEditor` (exposed `DrawNullableField`), `Keys.xml`.

Remaining before "done": in-game verification — build a save with each source mod (whole-stockpile
cap, refill %, Stack Gap per-item caps, a linked group), remove it, add PSC, load; confirm the import,
the letter/log fire once, hauling respects the imported limits, idempotency (only `<psc>` remains
after a resave), the both-loaded guard (no migration), the `<psc>`-wins guard, the no-foreign-data
no-op, and PSC removal still degrades cleanly.

### M5 (part 2) - Storage modes / Flickable Storage (code complete; pending in-game verification)

Builds clean, `net48`, Harmony 2.4, 0 warnings. Four per-storage modes inspired by Mlie's Flickable
Storage (MIT) — **storage on** (Normal/vanilla), **storage off**, **accept only**, **retrieve
only** — reimplemented with PSC's own mechanism (no forbidden-flag writes). Full design in
`docs/04_PSC_DESIGN.md` §17.

- **Mechanism — two tighten-only patches, no forbidden flag.**
  - *Freeze* (Off / AcceptOnly block haul-out **and** all consumption): postfix on
    `ForbidUtility.IsForbidden(Thing, Faction)` (`StorageMode_Patches.cs`) returns a **virtual**
    forbidden answer for items whose current unit is in a freeze mode — it never writes
    `CompForbiddable.Forbidden`. That `(Thing, Faction)` leaf is the chokepoint every player-side
    check routes through (the `(Thing, Pawn)` overload calls it), so cooks/doctors/refuelers/builders
    /haulers all honour it from one patch; a frozen item is auto-non-haulable, so no haul-out patch is
    needed. Explicit `Type[]` (ambiguous-match rule). Only-ever-adds forbidden (never un-forbids), so
    the player's manual forbid is preserved.
  - *Haul-in block* (Off / RetrieveOnly): a mode gate added to the `AllowedToAccept(Thing)` postfix
    in `Admission_Patches.cs`, guarded by `sourceIsTarget` (D16) so a unit's own contents are never
    flagged misplaced.
- **Gate.** `PscMapComponent.anyFreezeModeActive` (a unit in Off or AcceptOnly) gates the
  `IsForbidden` postfix — PSC's hottest seam — so a colony using no freeze mode (incl. one using only
  RetrieveOnly) pays nothing. Recomputed via the existing `RecomputeGate` helper in `UpdateTracking`
  / `RebuildTrackingFromStore`.
- **State + persistence.** `PscStorageData.mode` (`PscStorageMode` byte enum), scribed as `<mode>`
  under `<psc>` (written only when non-Normal). Included in `HasPersistentPolicy` and
  `CopyPolicyFrom`; shared across a linked `StorageGroup` automatically. Add/remove/delete safe by
  construction — nothing is left forbidden because the flag is never touched.
- **UI.** One `Command_Action` mode gizmo (`PscModeGizmo`) on zones + shelves via the existing
  `Feeder_Gizmos_Patch` `GetGizmos` postfixes; its icon reflects the current mode and a click opens a
  four-option `FloatMenu`. `SetMode` writes the mode, calls `NotifyPolicyChanged`, and on a freeze
  transition pokes `listerHaulables`/`listerMergeables` for the unit's contents (mirrors
  `CompForbiddable.Forbidden`'s setter) so the change is immediate. Four icons reused from Flickable
  (MIT, credited) at `Textures/UI/Mode/{On,Off,AcceptOnly,RetrieveOnly}.png`.
- **Known limits (documented in tooltips + README):** forbidden-ignoring pawns (mental break) bypass
  the freeze; mods reading `CompForbiddable.Forbidden` directly miss the virtual state; manually
  unforbidding one item in a frozen pile looks inert (release by switching the mode).

New files: `Source/Patches/StorageMode_Patches.cs`, `Source/UI/PscModeGizmo.cs`,
`Textures/UI/Mode/{On,Off,AcceptOnly,RetrieveOnly}.png`. Edits: `PscStorageData` (enum + `mode`),
`PscMapComponent` (`anyFreezeModeActive`), `Admission_Patches` (haul-in gate),
`Feeder_Gizmos_Patch` (yield mode gizmo), `Keys.xml`, `About.xml`, `README.md`, `AGENTS.md`.

Remaining before "done": in-game verification of the design §17 scenarios — Off/AcceptOnly freeze
(no haul-out, cooks/doctors won't take), RetrieveOnly drains but takes no intake, prompt mode-flip,
linked-group sharing, delete-while-frozen leaves nothing forbidden, save/remove round-trip, and the
no-mode performance early-out.
