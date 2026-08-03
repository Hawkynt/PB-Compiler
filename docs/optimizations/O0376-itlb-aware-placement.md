# O0376 — Instruction-TLB-aware placement

| | |
|---|---|
| **Status** | ⬜ Planned (386-era and later; meaningless in real mode) |
| **Stage** | Layout |
| **Related** | [O0374](O0374-hot-page-packing.md), [O0377](O0377-icache-set-aware-placement.md), [O0174](O0174-target-cost-models.md) |

## The idea

Keep mutually active blocks within **fewer instruction-TLB entries**. A hot loop
spread across many pages costs a TLB miss per page even when every page is
resident, and the TLB is far smaller than the cache.

## What it needs

- The TLB geometry from the target model
  ([O0174](O0174-target-cost-models.md)) — entry count and page size.
- The same placement machinery as [O0374](O0374-hot-page-packing.md), with a
  different granularity: TLB pressure counts *distinct pages simultaneously
  active*, not total pages touched.
- Real-mode DOS has no paging and no TLB, so this applies only to the hosted
  back ends or to a protected-mode target.
