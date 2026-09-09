# O0303 — Formatted-print specialization

| | |
|---|---|
| **Status** | ⬜ Planned (the whole-program constant case exists — [P0007](P0007-trivial-io-lowering.md)) |
| **Stage** | Emitter |
| **Related** | [P0007](P0007-trivial-io-lowering.md), [P0001](P0001-runtime-trimming.md), [R0003](R0003-string-engine.md) |

## The idea

`PRINT USING` and `PRINT` with mixed operands go through a general formatting
engine that interprets the format at run time. When the format is a **literal**,
the interpretation is compile-time work: emit a straight-line sequence of the
specific conversions the format calls for, and drop the engine from the image
entirely ([P0001](P0001-runtime-trimming.md)).

[P0007](P0007-trivial-io-lowering.md) already does the extreme case — a program
whose whole output is known — by precomputing the bytes.

## Applies to

```basic
DIM n%, s$
PRINT USING "###.##"; x!     ' a constant format, interpreted every call today
PRINT "n="; n%; " s="; s$
```

## What it needs

- A format parser at compile time producing a conversion plan, plus specialized
  emitters for the individual conversions (integer, fixed-point, string,
  padding).
- **Byte-exact output**: PB's number formatting, its 14-column comma zones, its
  rounding and its `USING` edge cases are the most fidelity-sensitive area of the
  whole runtime (`docs/QUIRKS.md`), so each specialized path has to be
  oracle-verified against the general one.
