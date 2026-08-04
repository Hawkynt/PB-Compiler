# O0062 — Loop rotation, IV simplification and fusion

| | |
|---|---|
| **Status** | 🟡 Partial — pre-tested `DO WHILE`/`DO UNTIL` loops rotate (both the plain and the register-resident paths); FOR-loop rotation, IV simplification and fusion remain |
| **Stage** | Mid-end / emitter |
| **Related** | [O0028](O0028-loop-invariant-code-motion.md), [O0030](O0030-induction-variable-strength-reduction.md), [O0007](O0007-loop-unrolling.md) |

## The idea

Three classic loop transforms that the current pipeline does not do:

1. **Rotation** — a pre-test loop (`test; body; jmp test`) becomes
   `if !test goto end; do body while test`, which costs one branch per iteration
   instead of two and makes the body a single block that LICM and CSE can treat
   as straight-line.
2. **Induction-variable simplification** — derived induction variables
   (`j = 2*i + 3` inside the loop) are rewritten as their own incrementally
   updated variables, and redundant ones are coalesced.
3. **Fusion** — two adjacent loops with the same trip count over the same arrays
   merge into one, halving the loop overhead and improving locality.

## Applies to

```basic
DIM i%, a%(0 TO 99), b%(0 TO 99)
FOR i% = 0 TO 99 : a%(i%) = i% : NEXT
FOR i% = 0 TO 99 : b%(i%) = a%(i%) * 2 : NEXT
```

## Today

Two loops, 200 iterations of loop overhead, and `a%()` is walked twice.

## Planned (fusion)

```basic
FOR i% = 0 TO 99
  a%(i%) = i%
  b%(i%) = a%(i%) * 2
NEXT
```

## Now — `DO` rotation

Under `$OPTIMIZE SPEED`, a pre-tested `DO WHILE`/`DO UNTIL … LOOP` (a pre-condition,
no post-condition) is emitted as one entry guard plus a bottom test:

```asm
    ; DO WHILE i < n
    <test; jump done if false>   ; entry guard, once
top:
    <body>
    <test; jump top if true>     ; bottom test - no per-iteration JMP
done:
```

`EmitDoLoopControl` (shared by the plain `EmitDoLoop` and the SI/DI
register-resident `TryEmitDoLoopInRegister`, so it fires for essentially every
`DO` loop) drops the unconditional `jmp top` each pass. The condition is evaluated
the **same N+1 times** — one entry, one after each body — so any side effect is
preserved exactly; only the jump disappears. A zero-trip loop is correctly skipped
by the entry guard. Verified byte-identical against the genuine oracle and
self-differential (rotated == the golden-faithful build) over `WHILE`, `UNTIL` and
zero-trip cases; a regression test confirms the bound is compared at both ends.

## Still planned

- **FOR-loop rotation** — the same win for `FOR`, deferred because the FOR
  termination (step-sign, silent 16-bit wrap, the post-loop counter value —
  QUIRK 2.28) is evaluated inside the top test, so rotating it must reproduce that
  exactly at the new bottom-test site.
- **Induction-variable simplification** — derived IVs (`j = 2*i + 3`) rewritten as
  their own incrementally-updated variables and redundant ones coalesced.
- **Fusion** needs a dependence test: the second loop may not read an element of
  `a%()` that the first writes at a *later* index (a backward dependence),
  and neither loop may have side effects that observe the interleaving (`PRINT`,
  file I/O, `TIMER`).
- The counter's post-loop value must be preserved in every case
  (increment-then-test, QUIRK 2.28).
