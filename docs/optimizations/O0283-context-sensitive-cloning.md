# O0283 — Context-sensitive cloning

| | |
|---|---|
| **Status** | ✅ Implemented (bounded caller-value specialization) |
| **Stage** | Whole-program |
| **Related** | [O0160](O0160-call-site-cloning.md), [O0158](O0158-interprocedural-range-propagation.md), [O0269](O0269-profile-guided-inlining.md) |

## The idea

Interprocedural facts are **joined** over all callers, so one imprecise caller
destroys the precision for everybody. Cloning a procedure for an important
caller keeps that caller's facts intact.

Where [O0160](O0160-call-site-cloning.md) clones by a *property* (range,
alignment, aliasing), this clones by *caller identity* — the distinction that
matters when a hot caller and a cold one disagree about everything.

## Applies to

```basic
SUB Draw(BYVAL x%, BYVAL y%)
  ...
END SUB

' hot caller, always in-bounds coordinates
FOR i% = 0 TO 319 : CALL Draw(i%, 100) : NEXT
' cold caller, arbitrary coordinates
CALL Draw(userX%, userY%)
```

## Implementation

`Ir/Passes/ContextSensitiveCloning.cs` implements the bounded useful slice the
current IR can prove without profile metadata:

- direct calls are grouped by **caller identity**;
- a caller context is eligible when all of that caller's calls agree on at
  least one compile-time argument value while the complete direct-call set does
  not agree on that value;
- integer and floating constants, null pointers and global/function addresses
  are usable context values;
- the agreed values are seeded directly into an `IrCloner` copy, and only that
  caller group's callee operands are rebound to the copy;
- the original function is retained as the general entry for every other or
  unknown caller. Address escape therefore does not require a closed-world
  assumption;
- recursive calls inside a clone deliberately keep targeting the original body,
  because a recursive entry has not proved the clone's caller facts.

A fact shared by every direct caller is deliberately not cloned: O0018/O0159
interprocedural constant propagation can use that fact without paying for
another body.

## Generated definitions and ABI

An O0283 clone is a real function definition, not merely an optimizer-side copy
that disappears before emission. It therefore has to preserve the **callee side**
of the procedure ABI as well as the `IrCall` metadata on its callers.

`IrFunction.Convention` carries that definition identity through cloning. The IR
verifier rejects a direct call whose explicitly bound convention disagrees with
its callee, and `IrPrinter` includes the convention on definitions so an IR dump
cannot hide an ABI mismatch.

For the x86-16 routed backend, `X86CallAbi.TryDefinitionStackLayout` derives a
private generated definition's incoming `[BP+offset]` parameter layout and stack
cleanup directly from its IR signature and convention:

- BASIC/PASCAL: left-to-right stack arguments, callee cleanup;
- CDECL: right-to-left stack arguments, caller cleanup;
- STDCALL: right-to-left stack arguments, callee cleanup;
- FASTCALL/WATCALL: deliberately declined until generated definitions have an
  explicit incoming-register spill plan.

Generated definitions receive private assembler labels and go through the same
selection, scheduling and register-allocation stages as source procedures. A
clone is retained only when its original source definition and its other defined
callees also route successfully; otherwise callers fall back coherently instead
of splitting one procedure family's storage/ABI between the IR and direct
emitters.

## Pipeline and budget

O0283 is enabled only under `$OPTIMIZE SPEED`, where code growth is an explicit
trade. The SPEED pipeline first inlines calls already below the normal inliner
budget, then runs context cloning on the calls that survived. The clone receives
another ordinary function-optimization sweep; a second inliner pass can consume
a specialized body if the seeded facts made it small enough.

The current static limits are:

- at most **3 clones per source function**;
- at most **1024 cloned IR instructions per module**;
- when every visible caller has a useful context, the least-profitable caller
  stays on the original body rather than creating one clone per caller;
- `NOINLINE`, varargs, error-handler and inline-assembly bodies are not cloned.

There are no profile edge counts in the IR yet, so candidates are ranked
deterministically by `(number of proven arguments × direct call sites)`. Once
[O0268](O0268-profile-collection.md) / [O0269](O0269-profile-guided-inlining.md)
provide measured hotness, that weight can replace the static ranking without
changing the legality proof or rebinding mechanism.
