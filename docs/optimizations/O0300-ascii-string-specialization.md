# O0300 — ASCII string specialization

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Runtime + emitter |
| **Related** | [O0154](O0154-swar-search.md), [O0153](O0153-swar-arithmetic.md), [R0003](R0003-string-engine.md) |

## The idea

`UCASE$`, `LCASE$` and case-insensitive comparison have to consider the whole
byte range, including the DOS code-page characters above 127. When analysis (or
a `$OPTION`) establishes that the data is **7-bit ASCII**, the case operations
become a single arithmetic test per byte — and then a SWAR sequence over four
bytes at a time ([O0153](O0153-swar-arithmetic.md)).

## Applies to

```basic
DIM s$
s$ = UCASE$(s$)
```

## What it needs

- A source of the fact: a `$OPTION ASCII` declaration is the honest one, since
  proving it about arbitrary input data is generally impossible; a literal or a
  `DATA`-sourced string can be proven outright.
- The specialized routines, with the general path retained for everything else —
  a wrong assumption here silently corrupts national characters, which is
  exactly the kind of bug a DOS-era corpus would surface late.
