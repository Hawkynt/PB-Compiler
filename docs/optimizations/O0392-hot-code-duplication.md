# O0392 — Hot-code duplication

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout + mid-end |
| **Related** | [O0390](O0390-superblock-side-entry.md), [O0391](O0391-cold-code-deduplication.md), [O0095](O0095-branch-tail-merging.md) |

## The idea

The exact opposite of deduplication, applied where the temperature is opposite:
a small **shared hot** block reached from two places costs a jump from at least
one of them, and often a hot-to-cold transfer. Duplicating it into both
predecessors removes the branch and keeps each copy adjacent to its user.

Merging and duplicating are the same trade seen from two temperatures — which is
why they need one policy, not two independent passes
([O0399](O0399-profile-weighted-tail-merging.md)).

## Applies to

A shared loop-exit tail, a common increment sequence, a small epilogue reached
from two hot arms ([O0103](O0103-shared-epilogue.md) chooses the other way for
cold ones).

## What it needs

- A size and temperature threshold, and the CFG maintenance of
  [O0390](O0390-superblock-side-entry.md).
- Honesty about the interaction: duplicating hot code grows the hot working set
  ([O0375](O0375-working-set-minimization.md)), so the win has to be measured
  rather than assumed.
