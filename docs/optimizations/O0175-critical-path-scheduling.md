# O0175 — Latency- and port-aware scheduling

| | |
|---|---|
| **Status** | ⬜ Planned (the scheduler exists but orders by a fixed heuristic — [O0038](O0038-instruction-scheduling.md)) |
| **Stage** | Assembler scheduling |
| **Related** | [O0038](O0038-instruction-scheduling.md), [O0174](O0174-target-cost-models.md), [O0176](O0176-register-pressure-scheduling.md) |

## The idea

[O0038](O0038-instruction-scheduling.md) builds a real dependency partial order
and then picks *a* topological order with a simple rule: issue loads first,
cluster memory and ALU work. A cost-model-driven scheduler instead:

- prioritizes by **critical-path depth** — the instruction whose dependents are
  deepest goes first;
- balances **execution-port pressure** on superscalar targets, choosing between
  equivalent instruction forms that use different ports;
- accounts for **address-generation** pressure, precomputing or simplifying
  addresses when the AGU would saturate;
- speculates on **memory dependencies** where the target supports recovery or
  the compiler can emit a guard.

## Applies to

Every scheduling window; the source is irrelevant.

## Today

```
issue loads first, then cluster ALU work — one heuristic for every target
```

## Planned

```
priority(i) = f(critical-path depth, latency, port availability, register pressure)
```

with the parameters supplied per target.

## What it needs

- [O0174](O0174-target-cost-models.md) for the latency and port tables. On an
  8086 the correct model is nearly trivial (one execution unit, one bus unit),
  which is itself worth encoding — the current heuristic is a reasonable 8086
  model that happens to be applied everywhere.
- Register-pressure feedback, or aggressive load hoisting causes spills
  ([O0176](O0176-register-pressure-scheduling.md)).
