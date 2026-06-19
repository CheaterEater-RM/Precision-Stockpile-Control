# Precision Stockpile Control

Precision Stockpile Control is a RimWorld 1.6 mod planned around opt-in, low-overhead stockpile controls.

## Features

- Per-item stockpile maximums and refill thresholds (per stockpile / shelf / linked group)
- Batch hauling, both directions — **batch fill** (never *add* fewer than N items in a trip) and **batch empty** (never *remove* fewer than N items in a trip)
- Feeder source/destination links between stockpiles
- Fine-grained ordering within a priority band: a–z subpriority and an optional 1–10 priority mode
- Pick Up And Haul and LWM Deep Storage / multi-stack aware

### About "maximum" (please read)

The maximum is enforced on **hauling**: pawns keep each item at or below its maximum when they haul
it into storage (this also covers crafted products, which are hauled from the workbench). In a few
uncommon cases an item can be placed *directly* into a stockpile without being hauled — most often a
**workbench standing inside the stockpile** dropping its product on the spot, plus map-generation
scatter and some other mods' direct spawns. Those drops can briefly exceed the maximum; normal
hauling then drains the excess if a better spot exists. A future update may close this gap.

### About batch hauling

Each stockpile has a **batch fill** and a **batch empty** value (0 = off). Batch fill makes pawns wait
until they can bring at least that many items in one trip before hauling *into* the storage; batch empty
makes them wait until they can take at least that many *out* in one trip. Either way, amounts below the
threshold simply stay where they are until more accumulates — that's the point ("no tiny trips"), so a
stockpile that never reaches the threshold keeps those items. With inventory-haul mods (Pick Up And Haul,
Hauler's Dream) the threshold is enforced per source stack as items are picked up; their bulk trips don't
run PSC's final trip-size backstop, so enforcement there is per-stack rather than per-combined-trip.

Save compatibility: PSC is safe to add to or remove from an in-progress save. Added, it does nothing
until you set a limit. Removed, limited items simply become "allowed" (unlimited) again.

## Development Status

M1 (core limits), M2 (focused hard cap, batch, Pick Up And Haul / multi-stack integration), M3
(feeder links), and M4 (fine ordering — a–z subpriority and 1–10 priority) are code complete and
pending in-game verification. The authoritative design is in `docs/04_PSC_DESIGN.md`; build progress is
tracked in `MILESTONES.md`.

## License

MIT
