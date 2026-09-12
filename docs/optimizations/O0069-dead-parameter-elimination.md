# O0069 — Dead parameters and call-shape cloning

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Whole-program analysis + emitter |
| **Related** | [O0018](O0018-interprocedural-constant-propagation.md), [O0021](O0021-register-parameters.md), [O0022](O0022-dead-procedure-elimination.md) |
| **IR** | ✅ `DeadParameterElimination` shrinks owned signatures/calls to a fixpoint and emits one bounded strict-majority literal-shape clone when profitable. |

## The idea

Two interprocedural transforms that pick up where
[O0018](O0018-interprocedural-constant-propagation.md) stops. IPCP specializes a
callee body but deliberately leaves the ABI alone; these change it:

1. **Dead-parameter elimination** — a parameter no reachable path in the callee
   reads is removed from the signature and from every call site, so its push and
   frame slot disappear. Pure argument-production work becomes ordinary DCE;
   effectful argument expressions remain evaluated in their original position
   and order. (When IPCP has just replaced every read with a literal, the
   parameter is dead *by construction* — that is the common case.)
2. **Call-shape cloning** — a procedure called with one dominant literal argument
   shape gets a specialized clone for that shape while the general body remains
   for the other call sites.

## Applies to

```basic
SUB Draw(BYVAL mode%, BYVAL x%)
  IF mode% = 1 THEN PRINT "text"; x% ELSE PRINT "gfx"; x%
END SUB

CALL Draw(1, 10)
CALL Draw(1, 20)
```

## Implemented

After IPCP replaces every use of `mode%` by `1`, O0069 removes `mode%` from the
owned function signature and every direct call. Surviving formal arguments are
renumbered, so backends see the new ABI rather than stale frame offsets.

```asm
    mov     ax, 000Ah
    push    ax
    call    Draw             ; one argument
    mov     ax, 0014h
    push    ax
    call    Draw
    ...
Draw:
    ...                      ; RET 2 instead of RET 4
```

Deadness is solved to a fixpoint. If `bridge(x)` only forwards `x` to a callee
whose parameter is removed, `bridge`'s `x` becomes dead and is removed on the
same O0069 run.

Call-shape cloning is deliberately narrower than
[O0160](O0160-call-site-cloning.md). O0069 recognizes only literal shapes, needs
a strict majority of the visible calls, and creates at most one clone per
procedure. The duplicated body is capped at 24 IR instructions, and a
conservative target-neutral size proxy requires the number of call operands
removed by specialization to cover the number of duplicated IR instructions.
Range/alignment/alias versioning remains O0160 work.

## Equivalent BASIC

```basic
SUB Draw(BYVAL x%)
  PRINT "text"; x%
END SUB
CALL Draw(10)
CALL Draw(20)
```

## Safety boundary

- The same **ownership** proof [O0021](O0021-register-parameters.md) uses: every
  use must be an owned direct call. The entry point, escaped function addresses,
  varargs, inline assembly and non-local error handling are not rewritten.
- Every call must exactly match the current signature before an ABI change is
  attempted; malformed or heterogeneous call shapes are declined.
- Argument **evaluation order and side effects** are preserved by removing only
  the call operand. The IR instruction that produced the argument is not erased;
  later DCE may remove it only when it is pure and genuinely dead.
- Cloning uses the shared `IrCloner`, maps specialized parameters directly to
  their literals, keeps the original body for minority calls, and is bounded by
  the cost rules above.

## Reference / licensing

LLVM's `deadargelim` documentation and `DeadArgumentElimination` pass were used
as behavioral/design references for whole-program dead-argument rewriting and
call-chain propagation. LLVM is Apache-2.0 WITH LLVM-exception. PB-Compiler's
implementation is independently structured around its own IR and use lists; no
LLVM implementation code or comments were copied. No dependency was added.
