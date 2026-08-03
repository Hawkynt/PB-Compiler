# O0393 — Jump tables near their dispatch

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0029](O0029-select-jump-table.md), [O0394](O0394-literal-pool-placement.md), [O0374](O0374-hot-page-packing.md) |

## The idea

A jump table is **data read by hot code**: the dispatch loads from it on every
execution. Placing it far from the dispatch costs a second page or line for what
is logically part of the instruction sequence.

Keeping the table adjacent to the block that indexes it makes the whole dispatch
— range check, index, table, jump — one locality unit.

## What it needs

- Data placement to participate in the layout, not just code — which means the
  image writer must be able to interleave read-only data with code fragments
  rather than pooling all data at the end.
- Care in the emitted image: a table placed inside the code stream must not be
  *executed*, so it needs to sit after an unconditional transfer — which is
  exactly where the dispatch's own jump leaves it.
