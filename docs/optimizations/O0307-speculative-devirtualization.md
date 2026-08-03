# O0307 — Speculative devirtualization

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program |
| **Related** | [O0271](O0271-indirect-call-promotion.md), [O0279](O0279-whole-program-devirtualization.md), [O0304](O0304-guarded-specialization.md) |

## The idea

Where the target set of an indirect call is **not** provably complete, optimize
for the likely target anyway and keep the indirect call as the fallback. Unlike
[O0271](O0271-indirect-call-promotion.md), the guess need not come from a
profile: a static heuristic (only one procedure of that signature exists, or one
is assigned in the same procedure) is often enough.

## Applies to

```basic
DIM f AS FUNCTION(LONG) AS LONG
f = CODEPTR32(Double&)
' ... f may be reassigned somewhere the compiler cannot see
PRINT f(21)
```

```text
if f = CODEPTR32(Double&) then  <inlined Double&>  else  call [f]
```

## What it needs

- The guard-and-fallback structure ([O0304](O0304-guarded-specialization.md)).
- A heuristic for picking the candidate when no profile exists, and the honesty
  to keep the compare cheap — one `CMP`/`JNE` against a link-time constant.
- The wider prize: enough devirtualization to lift the program-wide **disable**
  a single address-taken procedure currently imposes on IPCP
  ([O0018](O0018-interprocedural-constant-propagation.md)) and register
  parameters ([O0021](O0021-register-parameters.md)).
