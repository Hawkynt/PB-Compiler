# O0375 — Working-set minimization

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0374](O0374-hot-page-packing.md), [O0373](O0373-phase-aware-layout.md), [O0406](O0406-layout-assertion-battery.md) |

## The idea

Minimize the number of code pages **touched during a representative workload** —
not the binary's size. The two objectives diverge: a smaller binary with its hot
code scattered touches more pages than a larger one with its hot code packed.

Stating the objective this way is what separates layout optimization from size
optimization, and it is the metric the battery should assert
([O0406](O0406-layout-assertion-battery.md)).

## What it needs

- A page-touch simulation over the profile: replay the block trace against a
  candidate layout and count distinct pages.
- The placement search itself ([O0374](O0374-hot-page-packing.md)), with this as
  its cost function.
- On DOS the analogous measurable is **code bytes fetched** and **taken
  transfers**, which the same simulation can produce from the same trace.
