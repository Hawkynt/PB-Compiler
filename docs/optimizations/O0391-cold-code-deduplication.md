# O0391 — Cold-code deduplication

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0040](O0040-identical-code-folding.md), [O0095](O0095-branch-tail-merging.md), [O0399](O0399-profile-weighted-tail-merging.md) |

## The idea

Identical error and cleanup sequences are merged **aggressively** when they are
cold: runtime speed is irrelevant there, and every byte removed is a byte the
hot region does not have to share a page with.

[O0040](O0040-identical-code-folding.md) already folds byte-identical regions
under `$OPTIMIZE SIZE`. The refinement is to apply it by **temperature** rather
than globally — merge the cold ones always, leave the hot ones alone.

## Applies to

The Error-9 and Error-6 stubs, `TRY`/`FINALLY` cleanup tails, and the
`PRINT "..." : END` sequences that DOS-era code repeats per failure case.

## What it needs

- The congruence test of [O0040](O0040-identical-code-folding.md) plus a
  temperature signal ([O0268](O0268-profile-collection.md)) — or the structural
  classification that error paths are cold
  ([O0367](O0367-exception-handler-outlining.md)).
- The merged copy lives in the cold region, so the extra jump it costs is paid
  only on paths that were already exceptional.
