# O0279 — Whole-program devirtualization

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program |
| **Related** | [O0271](O0271-indirect-call-promotion.md), [O0307](O0307-speculative-devirtualization.md), [O0022](O0022-dead-procedure-elimination.md) |

## The idea

When the **complete set** of possible targets of an indirect call is known, the
call can be resolved statically. PB has no vtables, but it has procedure
pointers — `CODEPTR32`, typed delegates, lambdas — and the compiler already
records every one of them in `CallBindings` for reachability.

If exactly one procedure's address is ever stored into a delegate, the call
through it *is* a direct call, with no guard needed.

## Applies to

```basic
DIM f AS FUNCTION(LONG) AS LONG      ' pb36 typed procedure pointer
f = CODEPTR32(Double&)               ' the only assignment in the program
PRINT f(21)                          ' provably Double&
```

## What it needs

- The address-taken census the reachability walk already builds, refined from
  "which procedures are address-taken at all" to "which addresses can reach
  *this* variable" — a small points-to analysis over handles.
- With more than one candidate but a complete set, a compare chain or jump table
  beats an indirect call; with an incomplete set, the guarded form
  ([O0271](O0271-indirect-call-promotion.md)) applies instead.
- Devirtualizing every call would also lift the program-wide **disable** that a
  single `CODEPTR` currently imposes on IPCP and register parameters.
