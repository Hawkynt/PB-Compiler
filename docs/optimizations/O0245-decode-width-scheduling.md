# O0245 — Decode-width-aware scheduling

| | |
|---|---|
| **Status** | ⬜ Planned (no effect before a superscalar target) |
| **Stage** | Assembler scheduling |
| **Related** | [O0092](O0092-encoding-selection.md), [O0038](O0038-instruction-scheduling.md), [O0174](O0174-target-cost-models.md) |
| **Split from** | [O0092](O0092-encoding-selection.md) |

## The idea

A superscalar front end decodes a limited number of instructions per cycle, and
only in certain length and complexity combinations. Arranging the instruction
mix so the decoders stay fed — avoiding awkward boundaries and long-prefix
sequences — is a throughput win the dependency scheduler alone cannot deliver.

## What it needs

- Per-target decoder rules (how many, which slots accept complex instructions,
  length limits) in [O0174](O0174-target-cost-models.md).
- The scheduler must be able to express a **placement** preference, not only a
  dependency order — the same expressive gap [O0109](O0109-macro-fusion-placement.md)
  runs into.
- Irrelevant on 8086 through 486: there is one decode path, and the constraint
  there is instruction *bytes*, not instruction *shape*.
