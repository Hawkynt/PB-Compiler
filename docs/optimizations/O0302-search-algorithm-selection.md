# O0302 — Search algorithm selection by pattern

| | |
|---|---|
| **Status** | ⬜ Planned |
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

## What it needs

- Specialized runtime entry points, selected by the emitter from the pattern's
  compile-time properties.
- The exact `INSTR` semantics preserved in each: 1-based result, 0 for not
  found, the start-position argument, and the empty-pattern case (which differs
  between dialects — see `docs/BASIC-FAMILY.md`).
