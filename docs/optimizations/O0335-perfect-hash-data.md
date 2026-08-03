# O0335 — Perfect-hash generation for static key sets

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0100](O0100-perfect-hash-dispatch.md), [O0334](O0334-binary-search-recognition.md), [O0336](O0336-fsm-compilation.md) |

## The idea

A fixed set of keys — keyword tables, enum names, command strings, file
extensions — admits a **collision-free** hash computed at compile time. Lookup
becomes: hash, index, verify. Constant time, no search, no table of comparisons.

Where [O0100](O0100-perfect-hash-dispatch.md) applies this to *control flow*
(dispatching to an arm), this applies it to *data* (finding a record).

## Applies to

```basic
DIM cmd$, i%
DATA "LIST", "LOAD", "SAVE", "RUN", "NEW", "DELETE"
FOR i% = 0 TO 5
  IF cmd$ = words$(i%) THEN EXIT FOR      ' up to six string comparisons
NEXT
```

## What it needs

- A hash search at compile time over a small parameter space, with a guaranteed
  fallback when none is found in budget.
- A **cheap hash for strings** on an 8086 — a length plus one or two characters
  is usually enough to separate a keyword set, and costs far less than a general
  hash over the whole string.
- The verifying comparison is mandatory: the hash is perfect only on the key
  set, so any other input must be rejected explicitly.
