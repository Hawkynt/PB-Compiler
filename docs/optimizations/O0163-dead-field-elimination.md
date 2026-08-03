# O0163 — Dead field and component elimination

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program analysis |
| **Related** | [O0023](O0023-dead-global-elimination.md), [O0059](O0059-scalar-replacement.md), [P0003](P0003-bss.md) |

## The idea

[O0023](O0023-dead-global-elimination.md) removes a whole global nothing reads.
The same argument applies **per field** of an internal, non-escaping `TYPE`, and
per component of an array of such types: a field that no reachable code reads
costs storage in every instance and a store at every write.

For an array of 10 000 records, dropping one unread `LONG` field saves 40 KB.

## Applies to

```basic
TYPE Particle
  x AS INTEGER
  y AS INTEGER
  debugId AS LONG            ' written at creation, never read
END TYPE
DIM p(0 TO 9999) AS Particle, i%
FOR i% = 0 TO 9999
  p(i%).debugId = i%
NEXT
```

## Today

40 000 bytes of storage and 10 000 stores for a field nothing reads.

## Planned

The field is removed from the layout, the stores disappear, and the record
shrinks from 8 bytes to 4 — which also halves the array's memory traffic.

## What it needs

- The **escape** condition is strict: the type must not be written to a file
  (`GET`/`PUT`), passed to an external unit, `LSET`/`FIELD`-mapped, overlaid via
  `DIM … AT`, addressed with `VARPTR`, or reached from inline asm — any of which
  makes the layout observable.
- Per-field read classification, exactly like
  [O0023](O0023-dead-global-elimination.md)'s: an occurrence is a read unless it
  is precisely the target of a top-level assignment.
- Layout recomputation, which the `pb36` layout control
  (`PACKED`/`ALIGN`/`AT`) must be allowed to veto.
