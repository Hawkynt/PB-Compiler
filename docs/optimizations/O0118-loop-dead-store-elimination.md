# O0118 — Loop dead-store elimination

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | SSA mid-end |
| **Related** | [O0002](O0002-dead-code-elimination.md), [O0048](O0048-ir-dead-store-elimination.md), [O0060](O0060-memory-ssa.md) |

## The idea

A store that the **next iteration** overwrites before anyone reads it is dead in
every iteration but the last. When only the final value can be observed, the
store sinks out of the loop and happens once.

The classic case is a scalar written each pass and used only after the loop —
which is also exactly the shape that keeps a value out of a register today.

## Applies to

```basic
DIM i%, last%, a%(0 TO 999)
FOR i% = 0 TO 999
  last% = a%(i%)             ' overwritten next iteration
NEXT
PRINT last%
```

## Today

1 000 stores to `last%`, 999 of them dead.

## Planned

```basic
FOR i% = 0 TO 999
  ' nothing
NEXT
last% = a%(999)              ' the only value that can be observed
```

More usefully, in the common accumulate shape the store stays in a register for
the whole loop and is flushed once — which is what
[O0005](O0005-register-residency.md) already achieves for the shapes it
recognizes, by a different route.

## What it needs

- Loop-carried liveness: the store is dead only if the variable is **not read**
  anywhere in the loop after the store (including through a call or an alias),
  and not read on any exit path taken before the last iteration.
- Memory stores (array elements) need [O0060](O0060-memory-ssa.md); scalars need
  only the existing SSA form extended across the back edge.
- `EXIT FOR`, `GOTO` out of the loop and error handlers all create exit paths
  where an intermediate value *is* observable.
