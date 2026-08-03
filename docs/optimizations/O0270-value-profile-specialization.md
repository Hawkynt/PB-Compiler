# O0270 — Value-profile specialization

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program |
| **Related** | [O0268](O0268-profile-collection.md), [O0160](O0160-call-site-cloning.md), [O0164](O0164-partial-evaluation.md), [O0304](O0304-guarded-specialization.md) |

## The idea

Record the **common runtime values** of selected arguments, then clone the
procedure for them. Where [O0018](O0018-interprocedural-constant-propagation.md)
requires *every* call site to agree at compile time, this needs only that one
value dominates at run time — and guards the specialized version with a test.

```basic
CALL Transform(mode%, data%)     ' mode% = 1 in 97% of executions
```

becomes a specialized `Transform_mode1` plus a fallback.

## What it needs

- Value profiling: a small histogram per instrumented argument, not just a count
  ([O0268](O0268-profile-collection.md)).
- The guard-and-fallback structure of
  [O0304](O0304-guarded-specialization.md) — the specialization is only valid
  under its condition.
- A cloning budget ([O0160](O0160-call-site-cloning.md)), since each
  specialization is a copy of the body.
