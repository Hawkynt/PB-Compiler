# O0336 — Finite-state-machine compilation

| | |
|---|---|
| **Status** | 🟡 Partial — dense byte classifiers compile to 256-entry class tables; multi-state machines remain planned |
| **Stage** | Mid-end |
| **Source** | `Ir/Passes/SwitchFormation.cs`, `Ir/Passes/FsmCompilation.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `FsmCompilationTests`, `StaticDispatchOptimizationTests` |
| **Related** | [O0029](O0029-select-jump-table.md), [O0154](O0154-swar-search.md), [O0335](O0335-perfect-hash-data.md) |

## The idea

Character-classification chains — repeated mutually exclusive tests on one byte — are a state machine
written as branches. Recovering the chain as one dispatch exposes its semantics; when many byte values
collapse to only a few outcomes, a **256-entry class table** then turns classification itself into one
indexed load plus a compact dispatch over class ids.

That representation is deliberately target-neutral. Sparse switches remain switches so the target can
still choose a range test, membership mask, jump table, perfect hash or decision tree instead of paying
256 bytes for a table that would buy little.

## Implemented

`SwitchFormation` first recognizes side-effect-free classification chains over one integer/byte subject,
including equality, ranges and supported Boolean combinations, and enumerates bounded sets into an
`IrSwitch` while preserving source-order / first-match semantics.

For an 8-bit switch, `FsmCompilation` then evaluates the recovered dispatch over **all 256 input bit
patterns** using `IrSwitch.TargetFor`. The resulting destination for each pattern is assigned a compact
byte class id and materialized as a private `.fsm.*` constant table. The original wide switch becomes:

```text
input byte
  -> zero-extend index
  -> load class = table[index]
  -> compact switch(class)
```

The exhaustive 256-value construction is the equivalence proof: explicit cases, the default path and
signed spellings such as `-1`/`255` all use the same fixed-width switch semantics as the original IR.

A table is emitted only when it has a plausible speed payoff: at least 16 explicitly named byte values,
no more than 16 destination classes, and at least a 2:1 reduction from named values to classes. Smaller
or highly fragmented classifiers stay in `IrSwitch` form for target-specific lowering.

## Applies to

```basic
IF c >= "0" AND c <= "9" THEN
  ...
ELSEIF c >= "A" AND c <= "Z" THEN
  ...
ELSEIF c >= "a" AND c <= "z" THEN
  ...
END IF
```

where the lowered tests are mutually exclusive checks of the same byte-valued subject.

## Still planned

- Multi-state machines whose arms update a recognizable state variable.
- Combining state and input into transition-table lookups.
- Vector/SWAR classification over multiple input bytes once the surrounding loop makes that profitable.
