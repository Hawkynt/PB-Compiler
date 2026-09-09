# O0271 — Indirect call promotion

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program |
| **Related** | [O0279](O0279-whole-program-devirtualization.md), [O0307](O0307-speculative-devirtualization.md), [O0268](O0268-profile-collection.md) |

## The idea

A `CALL DWORD` through a procedure pointer blocks everything: no inlining, no
interprocedural facts, and — in this compiler — it disables
[O0018](O0018-interprocedural-constant-propagation.md) and
[O0021](O0021-register-parameters.md) program-wide.

When the profile shows one target dominating, emit a compare against it:

```text
if target = CommonHandler then
    <inlined body of CommonHandler>
else
    call [target]
```

The hot path is direct and inlinable; the cold path keeps the indirect call, so
correctness does not depend on the profile being right.

## Applies to

```basic
DIM handler AS FUNCTION(LONG) AS LONG      ' pb36 typed procedure pointer
r& = handler(x&)
```

## What it needs

- Indirect-target profiling ([O0268](O0268-profile-collection.md)).
- The `CallBindings` record the compiler already keeps for
  `CODEPTR`/`CALL DWORD` references, to enumerate the candidate set.
- Where the candidate set is **provably complete**, no guard is needed at all —
  that is [O0279](O0279-whole-program-devirtualization.md).
