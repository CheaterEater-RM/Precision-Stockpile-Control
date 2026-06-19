# Precision Stockpile Control

Precision Stockpile Control is a RimWorld 1.6 mod planned around opt-in, low-overhead stockpile controls.

## Features

- Per-item stockpile maximums and refill thresholds (per stockpile / shelf / linked group)
- A per-stockpile **default limit** (applies to all items that don't have their own limit)
- Batch hauling, both directions — **batch fill** (never *add* fewer than N items in a trip) and **batch empty** (never *remove* fewer than N items in a trip)
- Feeder source/destination links between stockpiles
- Fine-grained ordering within a priority band: a–z subpriority and an optional 1–10 priority mode
- Pick Up And Haul and LWM Deep Storage / multi-stack aware
- **Imports your limits when you switch from another stockpile-limit mod** (one-way)

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

### About feeder links

Link storages so items flow from a **source** to a **destination** (the destination must be higher
priority than its source for hauling to happen). Two optional **auto-set priority** settings (both off
by default — one for connecting sources, one for connecting destinations) handle that priority for you:
when you connect storages, the one you paint is given a subpriority letter one step away from the one you
selected — a painted source drops one letter below it, a painted destination rises one letter above it —
so the link works immediately. It stops at the ends of the letter range and tells you when you need to
set the priority by hand.

### Switching from another stockpile-limit mod

PSC marks the mods below as incompatible — they set the same per-storage limits PSC does, so running
one alongside PSC double-enforces. You'll see a warning in the mod list if both are active (it's
advisory, not a hard block). The intended path is to **remove the old mod and let PSC take over**:
when you do, PSC reads its leftover settings out of your save and imports them automatically the
first time you load — so you don't have to set everything up again. This is **one-way** (PSC won't
convert back), and you'll get a one-time letter summarising what was imported. (If you ignore the
warning and keep both enabled, PSC skips the import entirely to avoid double-enforcing.) Some settings convert exactly, others approximately (whole-stockpile and percentage limits
don't map perfectly onto PSC's per-item model) — open a storage area's **PSC** panel to review or
tweak anything. Settings PSC can't express (per-stack-size caps, percentage-only Stack Gap setups)
are left out, and the import is skipped for any of these mods you still have enabled.

| Mod | Imported | Notes |
|---|---|---|
| **Stack Gap** | per-item caps + fill/refill % | per-item caps exact; the percentage settings convert approximately; multistack/similar-stack options dropped |
| **Satisfied Storage** | refill threshold | clean fit (refill-only) |
| **Variety Matters Stockpile** | duplicate-stack cap + refill % | approximate; per-stack-size cap dropped |

Storage Sorting is **not** supported — it's about *which* items a stockpile accepts (HP, quality,
rot), not limits, so there's nothing for PSC to import.

Save compatibility: PSC is safe to add to or remove from an in-progress save. Added, it does nothing
until you set a limit. Removed, limited items simply become "allowed" (unlimited) again.

## Development Status

M1 (core limits), M2 (focused hard cap, batch, Pick Up And Haul / multi-stack integration), M3
(feeder links), M4 (fine ordering — a–z subpriority and 1–10 priority), and M5 part 1 (one-way
migration from other limit mods + a per-stockpile default limit) are code complete and pending in-game
verification. Flickable Storage integration (M5 part 2) is still planned. The authoritative design is
in `docs/04_PSC_DESIGN.md`; build progress is tracked in `MILESTONES.md`.

## License

MIT
