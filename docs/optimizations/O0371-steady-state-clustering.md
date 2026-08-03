# O0371 — Steady-state code clustering

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0370](O0370-startup-code-clustering.md), [O0373](O0373-phase-aware-layout.md), [O0374](O0374-hot-page-packing.md) |

## The idea

The counterpart of startup clustering: the blocks executed **repeatedly after
initialization** — the main loop, the event dispatch, the inner computation —
are packed together, so the program's resident working set during normal
operation is as small as possible.

This is the set that determines the program's actual performance; everything
else is noise around it.

## What it needs

- The same phase signal as [O0370](O0370-startup-code-clustering.md), read the
  other way round.
- A packing objective ([O0374](O0374-hot-page-packing.md)) rather than merely an
  ordering one: the goal is *fewest pages touched*, not *shortest distance*.
- Honest measurement, since "the steady state" is workload-dependent and a
  single profiling run can mislead
  ([O0403](O0403-scenario-weighted-layout.md)).
