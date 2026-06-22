# Precision Stockpile Control

Precision Stockpile Control is a RimWorld 1.6 mod built around opt-in, low-overhead stockpile controls.

## Features

- Per-item stockpile maximums and refill thresholds (per stockpile / shelf / linked group)
- Batch hauling, both directions — **batch fill** (never *add* fewer than N items in a trip) and **batch empty** (never *remove* fewer than N items in a trip)
- Routes that feed items from one storage into another (source → destination)
- Four per-storage **modes** — normal / frozen / fill only / drain only (Flickable-style)
- **Stockpile alarms** — a message (or letter) when a storage gets too full or too empty, with anti-spam controls
- An on-map **storage overlay** — a bottom-right toggle floats each storage's limits / mode / alarm / routes at a glance
- Fine-grained ordering within a priority band: a–z subpriority and an optional 1–10 priority mode
- Pick Up And Haul and LWM Deep Storage / multi-stack aware
- **Imports your limits when you switch from another stockpile-limit mod** (one-way)

### About "maximum" (please read)

The maximum is enforced on **hauling**: pawns keep each item at or below its maximum when they haul
it into storage (this also covers crafted products, which are hauled from the workbench). In a few
uncommon cases an item can be placed *directly* into a stockpile without being hauled — most often a
**workbench standing inside the stockpile** dropping its product on the spot, plus map-generation
scatter, a downed hauler dropping its load, and some other mods' direct spawns. Those drops can
briefly exceed the maximum. When that happens, **normal hauling drains the excess back out** to any
other storage that has room, stopping exactly at the maximum (this also applies when you *lower* a
limit below what a stockpile already holds). The items aren't trapped: only the brief over-the-max
*entry* is possible, not getting stuck above it. For a clean settle, give the stockpile a refill
threshold as well as a maximum.

### About batch hauling

Each stockpile has a **batch fill** and a **batch empty** value (0 = off). Batch fill makes pawns wait
until they can bring at least that many items in one trip before hauling *into* the storage; batch empty
makes them wait until they can take at least that many *out* in one trip. Either way, amounts below the
threshold simply stay where they are until more accumulates — that's the point ("no tiny trips"), so a
stockpile that never reaches the threshold keeps those items. With inventory-haul mods (Pick Up And Haul,
Hauler's Dream) the threshold is enforced per source stack as items are picked up; their bulk trips don't
run PSC's final trip-size backstop, so enforcement there is per-stack rather than per-combined-trip.

### About the search box

Open a storage area's **Stockpile Control** panel, type in the storage tab's search box, and use **Apply to search**
/ **Clear search limits** to set or clear a limit on every matching item at once. With no search active those
buttons read **Apply to all allowed** / **Clear all limits** and act on everything the storage currently allows.
The search matches by material/category as well as by name, so it catches items whose label doesn't contain the
word — searching `meat` includes beef and pork, `leather` includes birdskin, `wool` includes every wool. This
mirrors what the storage list itself shows when you search.

### About routes

Set a **route** so items flow from a **source** to a **destination** (the destination must be higher
priority than its source for hauling to happen). Two optional **auto-set priority** settings (both off
by default — one for setting sources, one for setting destinations) handle that priority for you:
when you set up a route, the storage you paint is given a subpriority letter one step away from the one
you selected — a painted source drops one letter below it, a painted destination rises one letter above
it — so the route works immediately. It stops at the ends of the letter range and tells you when you
need to set the priority by hand.

### About storage modes

Every stockpile and shelf gets a **mode** button (next to the route controls) with four settings:

- **Normal** — normal vanilla behaviour.
- **Frozen** — pawns won't haul items in, and items already here are *locked in place*: they won't be hauled out or used for cooking, crafting, doctoring, refueling, or building.
- **Fill only** — pawns haul items in as normal, but items here are locked (won't be hauled out or used). A collection pile that fills but never drains.
- **Drain only** — no new items hauled in, but pawns may freely haul out and use what's here. For draining a storage area you're emptying.

The freeze is handled the gentle way: PSC never actually flips an item's forbidden flag — it just answers "not usable right now" when the game asks. So it never overrides your manual forbid/allow toggles, nothing is left secretly forbidden if you delete the storage or remove the mod, and a linked storage group shares one mode. The freeze also only holds what the storage would accept: an item it does not accept (a disallowed type, or amounts over a per-item maximum) stays haulable, so over-cap or unwanted items still drain out through normal hauling instead of being trapped. Two things to know: manually *unforbidding* a single item inside a frozen pile has no effect (switch the pile's mode to release it), and a pawn in a mental break ignores the freeze. This makes Flickable Storage redundant if you run PSC — but the two are still compatible.

### About stockpile alarms

Every stockpile and shelf has an **alarm** button. Set a **high** alarm (warn when the storage is at
or above a chosen % full) and/or a **low** alarm (at or below a chosen % full), and PSC tells you when
it crosses the line. Fullness is measured by how many of the storage's stack-slots are occupied, so
"90% full" means nine of every ten slots hold something.

Controls to keep it from nagging:

- **Only alert if it lasts N hours** — a brief spike that drops back out before the time is up never
  fires. Leave it at 0 to alert right away.
- **Repeat** — *Once, then cancel* (a single alert, then the alarm disarms itself), *Once per day*, or
  *Once per quadrum*. An alarm re-arms on its own once the storage leaves the alarm range.
- **Notify with** — a transient on-screen **Message** (default) or a persistent **Letter**; either is
  clickable to jump to the storage.

Leave the message blank for a default that names the storage and its fill %, or write your own.
Right-click the alarm button to disarm just this storage, or every alarm on the map. A linked storage
group shares one alarm.

### Switching from another stockpile-limit mod

PSC marks the mods below as incompatible — they set the same per-storage limits PSC does, so running
one alongside PSC double-enforces. You'll see a warning in the mod list if both are active (it's
advisory, not a hard block). The intended path is to **remove the old mod and let PSC take over**:
when you do, PSC reads its leftover settings out of your save and imports them automatically the
first time you load — so you don't have to set everything up again. This is **one-way** (PSC won't
convert back), and you'll get a one-time letter summarising what was imported. (If you ignore the
warning and keep both enabled, PSC skips the import entirely to avoid double-enforcing.) Some settings convert exactly, others approximately (whole-stockpile and percentage limits
don't map perfectly onto PSC's per-item model) — open a storage area's **Stockpile Control** panel to review or
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
(routes), M4 (fine ordering — a–z subpriority and 1–10 priority), M5 part 1 (one-way migration from
other limit mods), and M5 part 2 (the four storage modes) are code complete and in-game verified. The
stockpile alarms and on-map storage overlay are code complete and in final testing. The authoritative
design is in `docs/DESIGN.md`.

## Credits

The storage-mode feature is inspired by **Flickable Storage** by **Mlie** (MIT), and reuses its four
mode icons. PSC reimplements the behaviour with its own mechanism (a read-side forbidden answer
rather than toggling the forbidden flag). Original mod: https://github.com/emipa606/FlickableStorage

## License

MIT
