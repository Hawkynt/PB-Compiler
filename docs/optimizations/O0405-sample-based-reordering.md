# O0405 — Sample-based binary reordering

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Post-link |
| **Related** | [O0276](O0276-post-link-optimization.md), [O0268](O0268-profile-collection.md), [O0404](O0404-stale-profile-matching.md) |

## The idea

Consume **sampled** execution data — a timer interrupt recording the instruction
pointer, or hardware branch history where it exists — taken from the final
optimized executable, rather than counts from an instrumented build.

Two advantages over instrumentation: no instrumentation bias (the counters
themselves change the layout and the timing they measure), and the profile
describes the binary that ships.

On DOS the mechanism is concrete: hook `INT 08h` or reprogram the PIT, record
CS:IP into a buffer, write it out at exit.

## What it needs

- The sampling hook and a buffer — a small, self-contained runtime addition.
- Address-to-block attribution, which needs the block map from
  ([O0360](O0360-basic-block-fragments.md)) — samples are addresses, and without
  a map they are unusable.
- Edge counts have to be **inferred** from a point sample, which is the standard
  sample-PGO problem: it gives block weights directly and edge weights only by
  flow conservation.
