# Precision Stockpile Control - Design Document

`04_PSC_DESIGN.md` is the authoritative design document for PSC.

This scaffold keeps a conventional `DESIGN.md` entry point at the mod root for tools and readers that expect it. Update this file only when the canonical design document is renamed, split, or superseded.

## Summary

PSC adds opt-in stockpile controls for target maximums, refill thresholds, batch hauling, feeder links, and fine-grained ordering. The implementation plan prioritizes vanilla-compatible persistence, hot-path early-outs, count-cache maintenance, and careful Harmony patching.

## Current Scope

No implementation code has been added yet. See `MILESTONES.md` for the planned build slices.
