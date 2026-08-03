# O0399 — Profile-weighted tail merging

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0095](O0095-branch-tail-merging.md), [O0391](O0391-cold-code-deduplication.md), [O0392](O0392-hot-code-duplication.md) |

## The idea

Tail merging ([O0095](O0095-branch-tail-merging.md)) trades a jump for the
bytes of a duplicate. Whether that is a win depends entirely on temperature:

- **cold tails** — merge aggressively; the jump costs nothing and the bytes are
  a page saving ([O0391](O0391-cold-code-deduplication.md));
- **hot tails** — keep them separate, or even duplicate further
  ([O0392](O0392-hot-code-duplication.md)); the extra jump is a taken transfer
  on a hot path.

One policy, driven by weight, instead of one global switch.

## What it needs

- Block temperatures ([O0268](O0268-profile-collection.md)), or the structural
  approximation that error and exit tails are cold.
- The congruence machinery already used by
  [O0040](O0040-identical-code-folding.md).
- A single arbiter for merge-versus-duplicate, so the two passes cannot fight
  each other across recompiles.
