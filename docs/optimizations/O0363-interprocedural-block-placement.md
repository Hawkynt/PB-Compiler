# O0363 — Interprocedural basic-block placement

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Linker layout |
| **Related** | [O0360](O0360-basic-block-fragments.md), [O0366](O0366-hot-cold-function-splitting.md), [O0386](O0386-caller-callee-colocation.md) |

## The idea

Stop treating a procedure as an indivisible unit. Once blocks are placeable
fragments ([O0360](O0360-basic-block-fragments.md)), the hot blocks of *several*
procedures can be laid out together — and the cold blocks of the same procedures
banished elsewhere.

The hot working set then contains the hot **code**, not the hot **functions**,
which is a substantially smaller thing.

## What it needs

- Block-level counts ([O0268](O0268-profile-collection.md)).
- A placement algorithm over blocks with the constraint that every fragment's
  fixups stay resolvable — no reachability may be lost by distance
  ([O0384](O0384-branch-island-minimization.md)).
- Debug/listing output must still be able to describe a procedure whose bytes
  are no longer contiguous ([O0406](O0406-layout-assertion-battery.md)).
