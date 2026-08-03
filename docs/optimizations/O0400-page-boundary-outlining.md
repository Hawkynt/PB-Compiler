# O0400 — Page-boundary outlining

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0374](O0374-hot-page-packing.md), [O0402](O0402-layout-aware-outlining.md), [O0366](O0366-hot-cold-function-splitting.md) |

## The idea

Outline a cold fragment **specifically because** keeping it would push an
otherwise hot procedure across a page boundary. The decision is not about the
fragment's own temperature but about the *boundary* it causes — a few cold bytes
in the wrong place cost a whole extra page of working set.

## What it needs

- The layout's page map, so the pass knows where the boundaries fall — which
  means outlining must be able to run *during* or *after* placement, not only
  before it.
- A cost comparison: the outlined fragment costs a jump and its own placement,
  against one page of residency
  ([O0375](O0375-working-set-minimization.md)).
- Real-mode DOS has no pages; the equivalent DOS-era objective is keeping a hot
  procedure within one 64 KiB segment and minimizing code-fetch traffic.
