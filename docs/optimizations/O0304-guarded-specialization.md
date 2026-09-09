# O0304 — Guarded specialization

| | |
|---|---|
| **Status** | ✅ Implemented — uniform guarded SESE-region versioning primitive |
| **Stage** | IR middle end; existing branch/arithmetic emitters need no new opcode |
| **IR** | `PowerBasic.Compiler/Ir/Passes/GuardedSpecialization.cs` |
| **Verified by** | `GuardedSpecializationTests`, `IrVerifier` |
| **Related** | [O0130](O0130-trip-count-versioning.md), [O0152](O0152-vector-alias-versioning.md), [O0270](O0270-value-profile-specialization.md), [O0310](O0310-side-exit-deoptimization.md) |

## The idea

Check a profitable assumption **once**, then execute a version compiled under
it:

```text
if arrays do not overlap and count >= 32 and both aligned then
    vector fast path
else
    general path
end
```

Ahead-of-time compilation can do most of what people think requires a JIT. It
merely needs guards and fallback paths — the assumption does not have to be
provable, only *checkable*.

## IR implementation

`GuardedSpecialization.TryVersion` is the common mechanism used by speculative
middle-end passes. A caller supplies a single-entry/single-exit region, one or
more `i1` predicates, and optionally values that may be replaced in the fast
copy because those replacements are logical consequences of the predicates.
The primitive then:

1. checks the region shape, guard availability and a cloned-instruction budget
   before changing the CFG;
2. clones the region through `IrCloner`, seeding every guard to `true` and any
   caller-supplied assumptions only in that clone;
3. inserts one guard block, combining multiple predicates with `and`, and
   branches to either the specialized clone or the original region;
4. keeps the original region as the fully general fallback; and
5. repairs entry phis, existing exit phis and direct SSA live-outs so both
   versions rejoin as valid IR.

The default budget is 96 cloned IR instructions. Callers may provide a smaller
or larger explicit budget when their profitability model has better
information.

No emitter-specific guard instruction is required: the result is ordinary
`and` plus `condbr`, already understood by every IR backend.

## Safety boundary

The primitive deliberately declines rather than guesses when the CFG does not
fit the versioning contract. In particular it rejects side entries, multiple
exit targets, address-taken region blocks, values that are not available at the
guard, live-outs that cannot be joined safely, armed error handlers, inline
assembly and candidates over the code-size budget.

That conservatism is what makes the fallback rule useful: a failed speculation
can cost performance, but it cannot change program behaviour because the
unspecialized region is still the path taken when the guard is false.

## Applies to

Every optimization on this list that is currently blocked by an unprovable but
runtime-checkable precondition: aliasing, alignment, trip count, argument
value, indirect target, overflow-freedom and narrow ranges. O0304 supplies the
versioning mechanism; the individual analyses and profitability decisions
remain the responsibility of passes such as loop or alias versioning.
