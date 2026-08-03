# O0287 — Stack promotion

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0260](O0260-escape-analysis.md), [O0286](O0286-allocation-elimination.md), [O0182](O0182-small-array-scalar-replacement.md) |

## The idea

A non-escaping dynamic allocation of **bounded size** can live in the frame
instead of the heap: no `StrMem`/`StrFree`, no descriptor-table slot, no
compaction pressure, and the epilogue reclaims it for free.

## Applies to

```basic
SUB Format(BYVAL n&)
  LOCAL t$
  t$ = STR$(n&)              ' at most 12 bytes, never leaves this procedure
  PRINT t$
END SUB
```

## What it needs

- [O0260](O0260-escape-analysis.md), plus a **size bound**: the value must fit a
  frame budget, which for `STR$` and friends is a small known maximum but for a
  general string is not.
- A representation that the string runtime accepts — a descriptor pointing into
  the frame — or the value must be handled entirely by generated code without
  ever entering the heap routines. This is the design decision the whole item
  hinges on.
- Interaction with `ON ERROR`: a frame-resident value must not be freed by the
  handler's cleanup path, which frees *handles*.
