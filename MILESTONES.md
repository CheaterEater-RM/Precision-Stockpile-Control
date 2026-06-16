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
  toggle), Clear all connections (right-click float-menu required).
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

## Planned

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
