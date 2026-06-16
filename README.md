# Precision Stockpile Control

Precision Stockpile Control is a RimWorld 1.6 mod planned around opt-in, low-overhead stockpile controls.

## Features

- Per-item stockpile maximums and refill thresholds (per stockpile / shelf / linked group)
- Batch hauling (never make a trip smaller than N)
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

Save compatibility: PSC is safe to add to or remove from an in-progress save. Added, it does nothing
until you set a limit. Removed, limited items simply become "allowed" (unlimited) again.

## Development Status

M1 (core limits), M2 (focused hard cap, batch, Pick Up And Haul / multi-stack integration), M3
(feeder links), and M4 (fine ordering — a–z subpriority and 1–10 priority) are code complete and
pending in-game verification. The authoritative design is in `04_PSC_DESIGN.md`; build progress is
tracked in `MILESTONES.md`.

## License

MIT
