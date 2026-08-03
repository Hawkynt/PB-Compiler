# O0380 — Selective function alignment

| | |
|---|---|
| **Status** | ⬜ Planned (refines [O0232](O0232-procedure-entry-alignment.md), which aligns every procedure) |
| **Stage** | Emitter / layout |
| **Related** | [O0232](O0232-procedure-entry-alignment.md), [O0374](O0374-hot-page-packing.md), [O0174](O0174-target-cost-models.md) |

## The idea

Aligning every procedure entry to 16 bytes costs, on average, eight bytes per
procedure. For a program with 200 procedures that is 1.6 KB of padding — which
on a 640 KiB machine, and inside a 64 KiB code segment, is not nothing.

Aligning by **profile weight** keeps the fetch benefit for the procedures that
are called often and returns the bytes for the rest.

## What it needs

- Call counts ([O0268](O0268-profile-collection.md)) — or, statically, the
  observation that a procedure called from inside a loop is worth aligning and
  one called once is not.
- The target's actual alignment benefit
  ([O0174](O0174-target-cost-models.md)): on an 8086 it is **zero**, so the
  blanket rule should already be off there, and the padding is pure loss.
