# O0325 — Array padding for alignment

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Data layout |
| **Related** | [O0139](O0139-alignment-versioning.md), [O0026](O0026-auto-vectorization.md), [O0252](O0252-safe-overread-versioning.md) |

## The idea

Two cheap layout choices remove two whole classes of run-time work:

1. **Align the base** of an array to the vector width, so no peeling loop is
   needed ([O0139](O0139-alignment-versioning.md));
2. **Round the length up** to a whole number of vectors, so the tail is a full
   vector of padding rather than a scalar remainder — and a widened load past the
   last real element is provably safe
   ([O0252](O0252-safe-overread-versioning.md)).

The compiler controls the layout of static arrays and of its own heap
allocations, so both are free to arrange.

## Applies to

```basic
$CPU 80586 MMX
DIM a%(0 TO 999)             ' 1 000 elements: 250 MMX vectors exactly, if aligned
```

## What it needs

- Alignment support in the data-section layout and in the array allocator.
- Padding **must not be observable**: `UBOUND` must still report the declared
  bound, `ERASE` must clear what the program can see, and a record written to a
  file must keep its declared size.
- The size cost is bounded by one vector per array — negligible for large
  arrays, and not worth it for tiny ones.
