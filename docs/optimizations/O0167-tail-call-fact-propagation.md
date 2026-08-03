# O0167 — Tail-call fact propagation and specialized tail loops

| | |
|---|---|
| **Status** | ⬜ Planned (the tail call itself is implemented — [O0014](O0014-tail-call-optimization.md)) |
| **Stage** | Mid-end |
| **Related** | [O0014](O0014-tail-call-optimization.md), [O0016](O0016-value-fact-analysis.md), [O0110](O0110-general-induction-variables.md) |

## The idea

[O0014](O0014-tail-call-optimization.md) turns a tail self-call into a jump back
to frame entry — a loop in every respect except that no loop pass knows it. Two
consequences:

1. **Facts should propagate into the tail call.** The constant, range, alignment
   and known-bit facts holding at the call site hold at the callee's entry;
   today the jump discards them.
2. **The resulting loop should be optimized as one.** Once tail recursion is a
   loop, induction-variable analysis, range narrowing, dead-store elimination
   and conditional elimination all apply — the recursive parameter `n - 1` is an
   induction variable like any other.

## Applies to

```basic
$ERROR BOUNDS ON
SUB Walk(BYVAL i%, BYVAL n%)
  IF i% > n% THEN EXIT SUB
  a%(i%) = i%                 ' checked, though i% is bounded by the recursion
  CALL Walk(i% + 1, n%)       ' tail call -> jump
END SUB
```

## Today

The tail call becomes a jump, but `i%`'s range is unknown at the top of each
"iteration", so the bounds check stays and nothing else applies.

## Planned

The rewritten loop is recognized as `FOR i% = i0 TO n%`, `i%` gets a range, and
the check is dropped ([O0016](O0016-value-fact-analysis.md)).

## What it needs

- Recognizing the jump-to-entry as a **back edge** in the CFG the SSA mid-end
  builds — today the builder bails on the construct entirely.
- Recurrence classification for the arguments
  ([O0168](O0168-recursive-argument-evolution.md)).
