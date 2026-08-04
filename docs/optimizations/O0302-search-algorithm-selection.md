# O0302 — Search algorithm selection by pattern

| | |
|---|---|
| **Status** | 🟡 Partial (the one-byte pattern selects a `REPNE SCASB` scan; the short/long constant patterns still use the general probe) |
| **Stage** | Emitter + runtime |
| **Related** | [O0154](O0154-swar-search.md), [O0330](O0330-library-call-recognition.md), [R0003](R0003-string-engine.md) |

## The idea

`INSTR` uses one algorithm for every pattern. The right algorithm depends on
properties the compiler often knows at compile time:

| Pattern | Strategy |
|---|---|
| one byte | a byte scan — SWAR or `REP SCASB` |
| short, constant | SWAR parallel compare of the first byte plus a verify |
| longer, constant | Boyer-Moore-Horspool with a compile-time-generated skip table |
| runtime pattern | the general two-way scan |

A constant pattern also means the **skip table is data**, generated at compile
time rather than built at run time on every call.

## Applies to

```basic
DIM s$, p%
p% = INSTR(s$, "x")          ' single byte
p% = INSTR(s$, "BEGIN")      ' short constant
```

## Now

The **one-byte** row of the table ships. `INSTR(s$, "c")` (and `INSTR(s$, CHR$(n))`)
with a single-character constant needle and no start position dispatches to
`rt_scanchar` (`EmitScanChar`), a `REPNE SCASB` hardware byte scan, instead of the
general per-position `REPE CMPSB` probe — and the one-byte needle is passed as a
value, so it is never allocated. It preserves `INSTR`'s exact semantics: the 1-based
position of the first occurrence, 0 when not found, 0 for an empty haystack, and any
byte value (a `CHR$(0)` needle searches for a NUL). `rt_scanchar` lives in its own
trimmed section referenced only by the optimized emitter, so the faithful build keeps
the general `rt_instr` byte-for-byte (golden gate 250/250). Verified by a
self-differential DOSBox run — the delimiter at several positions, at the string end,
a miss, an empty haystack, a `CHR$(44)` needle, and a literal haystack — identical to
`$OPTIMIZE OFF`. The start-position form, `VERIFY`, and `INSTR … ANY` keep their
existing paths.

## Still planned

- The **short/long constant** rows: SWAR first-byte compare + verify, and
  Boyer-Moore-Horspool with a compile-time skip table (data, not built per call).
- The one-byte scan for the **start-position** form `INSTR(k, s$, "c")`.

## What it needs

- Specialized runtime entry points, selected by the emitter from the pattern's
  compile-time properties.
- The exact `INSTR` semantics preserved in each: 1-based result, 0 for not
  found, the start-position argument, and the empty-pattern case (which differs
  between dialects — see `docs/BASIC-FAMILY.md`).
