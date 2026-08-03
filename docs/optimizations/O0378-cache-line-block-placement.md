# O0378 — Cache-line-aware block placement

| | |
|---|---|
| **Status** | ⬜ Planned (486 and later) |
| **Stage** | Layout |
| **Related** | [O0231](O0231-loop-top-alignment.md), [O0398](O0398-branch-target-alignment.md), [O0379](O0379-selective-loop-alignment.md) |

## The idea

Prevent a hot block's **entry** — a loop header, a branch target, a procedure
prologue — from straddling a cache-line boundary unnecessarily. A target that
begins three bytes before a line end costs two line fetches for its first
instruction.

This is finer-grained than alignment ([O0231](O0231-loop-top-alignment.md)):
rather than padding every hot loop to a boundary, it nudges only those that
would otherwise straddle one.

## What it needs

- Line size from the target model, and block sizes known at layout time.
- A padding budget: every nudge costs bytes, and past a point the extra fetch
  traffic outweighs the straddle it avoided
  ([O0379](O0379-selective-loop-alignment.md)).
- Nothing to do before the 486, where there is no line to straddle.
