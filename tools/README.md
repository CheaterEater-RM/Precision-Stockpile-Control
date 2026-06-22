# PSC log parser

`Player.log` is mostly other mods, vanilla chatter, and a ~20-line Unity stack trace after
every `Log.*` call. PSC's own output is a tiny slice of that. This tool keeps only PSC's
lines (every one is prefixed `[PSC]`, see `Source/Core/PscLog.cs`) and drops the rest,
leaving a clean trace you can actually read.

It is a lean tracing tool, not a stats tool: it extracts, filters, and tallies.

## Capturing a log first

PSC's diagnostic lines only appear when its developer logging is on:

1. Enable RimWorld dev mode (Options > turn on development mode).
2. Open PSC's mod settings; at the bottom (visible only in dev mode) turn on the developer
   logging toggle.
3. Reproduce the issue, then quit. The lines are in `Player.log`:
   `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log`

## Running it

**Easiest:** double-click `Parse PSC Log.bat`. It opens a file picker at the RimWorld log
folder; choose `Player.log` and read the trace (the window stays open).

**Command line** (`py` on Windows, `python`/`python3` elsewhere):

```
py parse_psc_log.py Player.log          # parse a file
py parse_psc_log.py < Player.log        # or pipe it in
py parse_psc_log.py --pick              # file dialog (same as the .bat)
```

Needs Python 3.7+ (standard library only; the file picker uses bundled tkinter).

## Filters (combine freely)

| Flag | Effect |
|---|---|
| `--only TAG[,TAG...]` | Only these subsystems, e.g. `--only link,feeder`. Tags: `link`, `feeder`, `order`, `migrate`, `migration`, `reserve`, `general`. |
| `-m, --match SUBSTR` | Only lines containing SUBSTR (case-insensitive). Best for following one stockpile: `-m Zone_1234`. Repeatable; ANDed. |
| `--problems` | Only error-ish lines (the always-on `Log.Error`/`Warning` diagnostics). |
| `--collapse` | Fold consecutive identical lines into `... (xN)`. |
| `-q, --quiet` | Footer tally only; skip the per-line trace. |

## Output

Each kept line is shown as `<gutter> <tag> | <message>`, where a `!` gutter marks a
problem line. A short footer tallies totals and per-tag counts. Example:

```
  link    | created Zone_1234 -> Zone_5678
  order   | same-band tiebreak Zone_1234 vs Zone_5678 -> a first
! general | Vanilla reflection seam not found: StorageSettings.AllowedToAccept

--------------------------------------------------------
PSC lines: 3    problems: 1
  link=1  order=1  general=1
```

The tag is derived from the message itself (the word before the first colon), so new PSC
log lines bucket automatically with no changes here.
