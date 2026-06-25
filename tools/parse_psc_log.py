#!/usr/bin/env python3
"""
parse_psc_log.py : pull Precision Stockpile Control (PSC) lines out of a RimWorld log.

RimWorld's Player.log is mostly other mods, vanilla chatter, and a ~20-line Unity stack
trace appended after every Log.* call. PSC's own output is a tiny slice of that. Every PSC
line is prefixed "[PSC]" (see Source/Core/PscLog.cs), so this tool keeps only those lines
and throws everything else away, leaving a clean, readable trace.

Most PSC lines are sub-tagged by subsystem with a leading "word:" token, e.g.
    [PSC] link: created Zone_1234 -> Zone_5678
    [PSC] order: same-band tiebreak A vs B -> a first
    [PSC] migrate: imported Stack Gap onto Zone_9 (...)
Always-on diagnostics (Log.Error/ErrorOnce/Warning) have no single-word tag, e.g.
    [PSC] Vanilla reflection seam not found: ...
    [PSC] DoCategory prefix failed: ...
Those bucket under "general" and are flagged as problems so real failures stand out.

This is a lean tracing tool, not a stats tool: it extracts, filters, and tallies. It does
not compute rates or per-unit breakdowns.

Usage:
    py parse_psc_log.py Player.log
    py parse_psc_log.py < Player.log
    py parse_psc_log.py --pick                 # file dialog (used by the .bat launcher)
    py parse_psc_log.py --only link,feeder     # only those subsystems
    py parse_psc_log.py -m Zone_1234           # only lines mentioning a storage/unit id
    py parse_psc_log.py --problems             # only error-ish lines
    py parse_psc_log.py -q Player.log          # footer tally only
    # On Windows, paste into the terminal then send EOF with Ctrl+Z, Enter.

Reads from the given file(s) if any are passed, otherwise from stdin.
"""

import argparse
import os
import re
import sys
from collections import Counter
from dataclasses import dataclass

# PSC lines carry "->" (ASCII) and tag words. The Windows console defaults to cp1252, which
# can mis-handle non-ASCII on stdin/stdout. Force UTF-8 on all three streams so piping a
# pasted log in and printing the trace out both survive the console code page.
for _stream in (sys.stdin, sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

# Every PSC line contains this literal. Lines without it are stack-trace junk or other mods.
PSC_PREFIX = "[PSC]"

# The subsystem tag is the token before the first ":" when it is a single bare word.
# "link: ..." -> "link"; "Vanilla reflection seam not found: ..." -> no bare-word tag.
TAG_RE = re.compile(r"^(?P<tag>\w+):\s")

# Lines matching any of these read as a failure/diagnostic worth surfacing.
PROBLEM_RE = re.compile(r"fail|error|missing|not found|exception|could not", re.IGNORECASE)

GENERAL_TAG = "general"


@dataclass
class Entry:
    tag: str        # subsystem bucket: link / feeder / order / migrate / reserve / general / ...
    body: str       # the PSC line with the "[PSC] " prefix stripped (full, used for matching)
    msg: str        # display text: body with the leading "tag: " removed when there is one
    problem: bool   # True if it reads as an error/diagnostic


def parse_psc_line(line: str) -> Entry | None:
    """Return an Entry for a PSC line, or None for anything that is not one."""
    line = line.rstrip("\n")
    idx = line.find(PSC_PREFIX)
    if idx == -1:
        return None
    # Slice from the tag so any leading timestamp/level prefix is dropped, then drop the
    # "[PSC] " marker itself for a clean, uniform line.
    body = line[idx + len(PSC_PREFIX):].lstrip()

    m = TAG_RE.match(body)
    if m:
        tag = m.group("tag")
        msg = body[m.end():]          # drop the redundant "tag: " now that it has a column
    else:
        tag = GENERAL_TAG
        msg = body
    problem = bool(PROBLEM_RE.search(body))
    return Entry(tag=tag, body=body, msg=msg, problem=problem)


def parse(lines) -> list[Entry]:
    out = []
    for line in lines:
        e = parse_psc_line(line)
        if e is not None:
            out.append(e)
    return out


def apply_filters(entries: list[Entry], only: set[str], match: list[str],
                  problems_only: bool) -> list[Entry]:
    """Keep entries passing every active filter (AND)."""
    result = entries
    if only:
        result = [e for e in result if e.tag in only]
    if problems_only:
        result = [e for e in result if e.problem]
    for needle in match:
        n = needle.lower()
        result = [e for e in result if n in e.body.lower()]
    return result


def print_entries(entries: list[Entry], collapse: bool) -> None:
    if not entries:
        print("(no PSC lines matched)")
        return
    tag_w = max((len(e.tag) for e in entries), default=4)

    def render(e: Entry) -> str:
        gutter = "!" if e.problem else " "
        return f"{gutter} {e.tag:<{tag_w}} | {e.msg}"

    if not collapse:
        for e in entries:
            print(render(e))
        return

    # Fold runs of identical rendered lines into "... (xN)".
    prev = None
    count = 0
    for e in entries:
        cur = render(e)
        if cur == prev:
            count += 1
            continue
        if prev is not None:
            print(prev + (f"   (x{count})" if count > 1 else ""))
        prev, count = cur, 1
    if prev is not None:
        print(prev + (f"   (x{count})" if count > 1 else ""))


def print_footer(entries: list[Entry]) -> None:
    print()
    print("-" * 56)
    total = len(entries)
    problems = sum(1 for e in entries if e.problem)
    print(f"PSC lines: {total}    problems: {problems}")
    if total:
        tags = Counter(e.tag for e in entries)
        # Stable, readable order: known subsystems first, then any others alphabetically.
        # Store-search-rewrite seams add: index (def->units prefilter), engine (store-search
        # engine), bulk / puah / hd (bulk-haul integrations). Only "index" is emitted today; the
        # rest land with their Phase 2+ seams but are pinned here so the order is stable when they do.
        known = ["link", "feeder", "order", "migrate", "migration", "reserve",
                 "index", "engine", "bulk", "puah", "hd", GENERAL_TAG]
        seen = set()
        parts = []
        for t in known:
            if tags.get(t):
                parts.append(f"{t}={tags[t]}")
                seen.add(t)
        for t in sorted(tags):
            if t not in seen:
                parts.append(f"{t}={tags[t]}")
        print("  " + "  ".join(parts))


def resolve_paths(args: list[str]) -> list[str]:
    """Map CLI args to real files, tolerating an unquoted path with spaces.

    RimWorld's log lives under 'AppData\\LocalLow\\Ludeon Studios\\RimWorld by Ludeon
    Studios\\Player.log', which is full of spaces. If the user forgets to quote it, the
    shell splits it into several args. So: if every arg is already a real file, use them
    as-is (the normal multi-file case); otherwise fall back to treating the whole arg list
    as one space-joined path.
    """
    if args and all(os.path.isfile(a) for a in args):
        return args
    joined = " ".join(args)
    if os.path.isfile(joined):
        return [joined]
    # Neither interpretation resolved: return the args so the open() loop reports the
    # specific path that failed.
    return args


def default_log_dir() -> str:
    """The standard RimWorld Player.log folder on this OS (best effort)."""
    if sys.platform == "win32":
        # %USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios
        home = os.environ.get("USERPROFILE", os.path.expanduser("~"))
        return os.path.join(home, "AppData", "LocalLow", "Ludeon Studios",
                            "RimWorld by Ludeon Studios")
    if sys.platform == "darwin":
        return os.path.expanduser("~/Library/Logs/Ludeon Studios/RimWorld by Ludeon Studios")
    # Linux (Unity writes to ~/.config/unity3d/<company>/<product>)
    return os.path.expanduser("~/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios")


def pick_file() -> str | None:
    """Open a native file-open dialog rooted at the RimWorld log folder. Returns the chosen
    path, or None if the user cancelled. Used by the double-click .bat launcher."""
    try:
        import tkinter as tk
        from tkinter import filedialog
    except ImportError:
        print("error: --pick needs tkinter (bundled with standard Python). "
              "Pass the log path as an argument instead.", file=sys.stderr)
        return None

    root = tk.Tk()
    root.withdraw()  # hide the empty root window; show only the dialog
    start_dir = default_log_dir()
    if not os.path.isdir(start_dir):
        start_dir = os.path.expanduser("~")
    path = filedialog.askopenfilename(
        title="Select a RimWorld log (Player.log) to parse for PSC lines",
        initialdir=start_dir,
        initialfile="Player.log",
        filetypes=[("Log files", "*.log"), ("Text files", "*.txt"), ("All files", "*.*")],
    )
    root.destroy()
    return path or None


def split_csv(values: list[str]) -> list[str]:
    """Flatten repeated and comma-joined option values into a clean list."""
    out = []
    for v in values or []:
        out.extend(part.strip() for part in v.split(",") if part.strip())
    return out


def main() -> int:
    ap = argparse.ArgumentParser(
        description="Pull Precision Stockpile Control lines out of a RimWorld log.")
    ap.add_argument("files", nargs="*", help="Log file(s); reads stdin if omitted. "
                    "A single unquoted path containing spaces is also accepted.")
    ap.add_argument("--only", action="append", default=[], metavar="TAG[,TAG...]",
                    help="Keep only these subsystems (e.g. --only link,feeder). Repeatable.")
    ap.add_argument("-m", "--match", action="append", default=[], metavar="SUBSTR",
                    help="Keep only lines containing SUBSTR (case-insensitive); good for "
                         "tracing one storage/unit id or def name. Repeatable; ANDed.")
    ap.add_argument("--problems", action="store_true",
                    help="Keep only error-ish lines (fail/error/missing/not found/...).")
    ap.add_argument("--collapse", action="store_true",
                    help="Fold consecutive identical lines into '... (xN)'.")
    ap.add_argument("-q", "--quiet", action="store_true",
                    help="Footer tally only; skip the per-line trace.")
    ap.add_argument("--pick", action="store_true",
                    help="Open a file-selection dialog (defaults to the RimWorld log "
                         "folder). Used by the double-click launcher.")
    args = ap.parse_args()

    if args.pick and not args.files:
        chosen = pick_file()
        if not chosen:
            print("No file selected.")
            return 0
        args.files = [chosen]

    if args.files:
        lines = []
        for path in resolve_paths(args.files):
            try:
                with open(path, "r", encoding="utf-8", errors="replace") as fh:
                    lines.extend(fh.readlines())
            except OSError as exc:
                print(f"error: cannot read {path}: {exc}", file=sys.stderr)
                return 1
    else:
        lines = sys.stdin.readlines()

    entries = apply_filters(
        parse(lines),
        only=set(split_csv(args.only)),
        match=split_csv(args.match),
        problems_only=args.problems,
    )

    if not args.quiet:
        print_entries(entries, collapse=args.collapse)
    print_footer(entries)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
