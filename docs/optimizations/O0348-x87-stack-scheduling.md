# O0348 — x87 stack scheduling

| | |
|---|---|
| **Status** | ⬜ Planned (the ordering pseudo-resource exists — [C0003](C0003-x87-scheduling.md)) |
| **Stage** | Emitter |
| **Related** | [C0003](C0003-x87-scheduling.md), [O0349](O0349-x87-value-retention.md), [O0013](O0013-promotion-lowering.md) |

## The idea

The x87 is a **stack** machine, so the evaluation order determines how many
`FXCH` instructions, spills and reloads a expression costs. Choosing an order
that keeps operands in the right stack positions — the classic Ershov-numbering
problem for a stack machine — removes them.

[C0003](C0003-x87-scheduling.md) makes the FPU instructions *schedulable around*
integer work; this is about the FPU sequence itself.

## Applies to

```basic
DIM a!, b!, c!, d!, r!
r! = (a! + b!) * (c! + d!)   ' two sub-trees competing for the stack top
```

## What it needs

- An evaluation-order chooser over the expression tree, aware of the eight-deep
  stack and of which forms take a memory operand directly (`FADD [mem]` costs no
  stack slot at all).
- The **exactness constraint**: reordering float operations changes nothing
  here, because the *operations* are unchanged — only where their operands sit.
  That is what separates this from [O0344](O0344-fp-reassociation.md), which is
  fast-math-only.
