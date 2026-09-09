# O0281 — Return structure reduction

| | |
|---|---|
| **Status** | 🟡 Partial — hidden-result-buffer regions that no known caller observes are no longer stored; multi-register returns remain O0282 |
| **Stage** | Whole-program |
| **IR** | `Ir/Passes/ReturnStructureReduction.cs` — closed-call-graph census over trailing `$sret` result buffers; registered before module inlining |
| **Related** | [O0280](O0280-argument-structure-reduction.md), [O0102](O0102-return-value-forwarding.md), [O0282](O0282-internal-calling-convention.md) |

## The idea

A `FUNCTION` returning a `TYPE` by value (or a tuple —
`FUNCTION DivMod(...) AS (LONG, LONG)`) writes the aggregate through a hidden
structure-return buffer. When callers only ever read one or two fields, stores
to the remaining regions are unnecessary. Once those stores disappear, ordinary
DCE can also remove the pure computations that existed only to produce them.

Returning the surviving fields in registers is deliberately separate. That
changes the internal ABI and belongs to [O0282](O0282-internal-calling-convention.md).
O0281 keeps the existing `$sret` convention intact and reduces the work done
behind it.

## Applies to

```basic
TYPE Stats
  total AS LONG
  count AS LONG
  worst AS LONG
END TYPE

FUNCTION Analyze(a%()) AS Stats
  ...
END FUNCTION

DIM s AS Stats
s = Analyze(data%())
PRINT s.total                ' only .total is ever read
```

For an IR-level `$sret` producer, if every direct caller reads only the region
holding `total`, writes to `count` and `worst` are removed. A side-effect-free
producer chain for those fields then dies in normal DCE; a call used to compute
one of them remains, because removing the result store does not make the call's
side effects unobservable.

## Proof boundaries

The pass applies only when the module can prove the complete observation set:

- the candidate is a defined `void` function with a trailing pointer parameter
  named `$sret`;
- every use of that function is a direct call from a function still present in
  the same module;
- every call supplies a same-sized local `alloca i8, N` as the result buffer;
- result-buffer uses are only constant byte-offset GEPs and scalar loads/stores;
- the buffer is not passed elsewhere, used as another argument of the same
  call, subjected to whole-object operations, or addressed dynamically;
- callee-side accesses through `$sret` obey the same constant-region rules;
- error-handler and inline-assembly functions are excluded because their hidden
  control/memory effects invalidate the census;
- pointer-sized fields are conservatively excluded because target-neutral IR
  does not know their storage width.

Caller reads are unioned across all call sites. A region read by any caller —
or read internally by the callee — remains live. Region overlap is treated as
observation, so a narrower load overlapping a wider store keeps that store.

## Pipeline placement

`return-structure-reduction` is the first standard module pass. Function-local
optimization runs before the module pass, but the result aggregate remains
materialized because passing it as `$sret` is an escape to local SROA/mem2reg.
O0281 then removes dead result stores and immediately triggers another function
fixpoint, allowing DCE/SCCP to collect newly dead producer chains.

It runs before SPEED inlining. Once an inliner absorbs the callee, the original
closed set of direct `$sret` calls may no longer exist, so doing the census after
inlining would miss the intended whole-program opportunity.

## Remaining scope

The current implementation operates on the IR `$sret` convention. The PB 3.6
binder already models UDT-returning functions with a hidden trailing BYREF
result parameter, but target-neutral IR lowering of those source functions is a
separate prerequisite before ordinary BASIC source can exercise this pass end to
end.

Multi-register return of the surviving fields is also intentionally not part of
this pass; that is [O0282](O0282-internal-calling-convention.md).

## Reference model

LLVM's SROA uses the same conservative principle: only completely analyzable,
non-escaping aggregate storage is split into scalar regions. LLVM's argument
promotion documentation also calls out the symmetric stored-to-argument case as
conceptually return promotion, while leaving multiple-return calling-convention
work separate. O0281 follows those behavioral constraints independently rather
than copying implementation code.
