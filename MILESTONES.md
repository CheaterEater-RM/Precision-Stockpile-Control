# Precision Stockpile Control - Milestones

## Completed

### M0 - Scaffold

- Created RimWorld mod directory structure: `About/`, `1.6/Assemblies/`, `Source/`, `Source/Patches/`, `Languages/English/Keyed/`
- Materialized template files with PSC identity values
- Left source implementation files out of the scaffold

## In Progress

No implementation milestone is active yet.

## Planned

### M1 - Core Limits

- StorageSettings-attached PSC data and persistence
- Count cache, demand index, and cache-maintenance hooks
- Upper/lower refill behavior and haul job clamping
- Initial PSC stockpile tab entry point and per-row feedback

Dependencies: scaffold complete.

### M2 - Hard Caps, Batch, and Integration

- Direct-drop and direct-placement hard-cap enforcement
- Batch hauling final-count enforcement
- Pick Up and Haul capacity integration
- LWM/Ogre/multi-stack capacity correctness

Dependencies: M1 complete.

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
