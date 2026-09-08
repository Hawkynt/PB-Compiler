# O0269 — Profile-guided inlining

| | |
|---|---|
| **Status** | ✅ Done (IR consumer; O0268 still supplies real profile data) |
| **Stage** | Whole-program IR middle-end |
| **IR** | `Ir/Passes/Inliner.cs` (`callEdgeCount`) |
| **Verified by** | `PowerBasic.Compiler.Tests/Ir/ProfileGuidedInliningTests.cs` |
| **Related** | [O0006](O0006-inlining.md), [O0053](O0053-ir-inliner.md), [O0268](O0268-profile-collection.md), [O0401](O0401-layout-aware-inlining.md) |

## The idea

Inline **hot** calls aggressively and leave cold ones alone. The static gate is
structural — a bounded callee body plus the ordinary correctness rules
([O0006](O0006-inlining.md), [O0053](O0053-ir-inliner.md)) — which is safe but
blind: it can inline a helper called once at startup and decline one called a
million times in a loop because its body is only slightly above the static
budget.

O0269 makes the budget a property of the **call site**, not just the callee.
`Inliner.Run` accepts an optional `Func<IrCall, ulong?> callEdgeCount` lookup. A
returned count is the observed executions of that exact edge; `null` means that
site has no profile and therefore keeps the existing static budget. An explicit
zero is a profiled edge that was never taken and earns no budget.

That abstraction is deliberate: [O0268](O0268-profile-collection.md) owns profile
files, stable identities and attaching counts after recompilation. The inliner
only consumes the count, so O0268 can change representation without changing the
optimization policy.

## Cost model

For a profiled site the earned IR-instruction budget is:

```text
payoff-per-execution = 2 + argument-count   // CALL + RET + argument transfers
weighted-payoff      = edge-count * payoff-per-execution
budget               = min(hard-cap, weighted-payoff / growth-penalty)
```

The ordinary objective uses a growth penalty of `8` and a hard cap of `512`
instructions. `$OPTIMIZE SPEED` uses `4` and `1024`, respectively. Multiplication
saturates before division, so even a `ulong.MaxValue` profile cannot overflow or
remove the hard cap.

The callee's IR instruction count is the code-growth proxy. That is a close fit
for this inliner: cloning preserves the callee's instruction count, each cloned
`ret` becomes one `br`, and the original `call` is replaced by the branch into
the cloned entry. The model is intentionally target-neutral; target layout/cache
cost belongs to [O0401](O0401-layout-aware-inlining.md).

With no profile for a site, behavior is unchanged: the static budgets remain 64
instructions normally and 256 under `$OPTIMIZE SPEED`.

## Applies to

```basic
FUNCTION Pixel%(BYVAL x%, BYVAL y%)      ' called 64 000 times: larger budget
  ...
END FUNCTION

SUB ShowHelp                              ' called once: profile can keep it out of line
  ...
END SUB
```

Two calls to the **same** procedure may therefore make different decisions: a
hot loop edge can inline while a cold setup/help edge stays a real call.

## Correctness gates do not move

Profile data changes profitability, never legality. The existing inliner still
rejects declarations, direct recursion, `NOINLINE`, error-handler bodies and
inline-assembly bodies/callers. A hot count cannot override any of those gates.

A profile also cannot cause unbounded growth: even an arbitrarily hot call is
limited by the 512/1024-instruction hard cap.

## Validation

`ProfileGuidedInliningTests` covers:

- a hot call above the ordinary 64-instruction budget becoming inlineable;
- a cold call below that static budget staying out of line;
- hot and cold edges to the same callee making different decisions;
- missing profile data preserving the static heuristic exactly;
- the hard growth cap under an extreme count;
- `NOINLINE` remaining absolute under an extreme count.

## Profile production

The middle-end consumer is complete. End-to-end `--profile-generate` /
`--profile-use`, sampled/instrumented collection, stable call-edge identities and
profile-file parsing remain [O0268](O0268-profile-collection.md); until that lands,
unit tests and compiler integrations can provide the `callEdgeCount` lookup
directly.
