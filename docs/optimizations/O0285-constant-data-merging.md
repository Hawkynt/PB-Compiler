# O0285 — Program-wide constant data merging

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Linker / image writer |
| **Related** | [O0011](O0011-literal-overlap-pooling.md), [O0040](O0040-identical-code-folding.md), [P0006](P0006-header-squeeze.md) |

## The idea

[O0011](O0011-literal-overlap-pooling.md) packs *string* literals within one
compilation. The same argument applies to every other block of initialized,
read-only data — `DATA` pools, lookup tables, `TYPE` initializers, format
strings — and, with [O0277](O0277-link-time-optimization.md), across linked
units.

Identical blobs share one copy; a blob contained in another shares its bytes.

## Applies to

```basic
' two units, each with its own copy of the same table
DATA 0, 1, 4, 9, 16, 25, 36, 49
```

## What it needs

- A content-addressed pass over all read-only data sections at link time, with
  containment and overlap matching as in the literal packer.
- The same **read-only proof**: a blob whose address escapes (via `VARPTR`,
  inline asm, or a writable alias) must keep its private copy.
- Alignment must be preserved for blobs whose consumers need it
  ([O0325](O0325-array-padding-alignment.md)).
