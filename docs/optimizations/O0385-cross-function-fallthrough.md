# O0385 — Cross-function fall-through

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0213](O0213-cross-procedure-tail-call.md), [O0388](O0388-tail-call-layout.md), [O0363](O0363-interprocedural-block-placement.md) |

## The idea

Where the ABI and the symbol rules permit, place two fragments so that execution
**flows directly** from one into the other without a jump at all. The clearest
case is the cross-procedure tail call
([O0213](O0213-cross-procedure-tail-call.md)): if `B` is laid out immediately
after `A`'s teardown, the `jmp B` disappears entirely.

## Applies to

```basic
SUB A
  ...
  CALL B(x%)                 ' tail call: becomes jmp B, or nothing at all
END SUB

SUB B(BYVAL x%)
  ...
END SUB
```

## What it needs

- Placeable fragments ([O0360](O0360-basic-block-fragments.md)) and the freedom
  to order procedures.
- The fall-through must be **the only way in** to that arrangement — B keeps its
  own entry label for other callers, so the merge is a layout adjacency, not a
  merged procedure.
- Alignment interacts: padding B's entry ([O0232](O0232-procedure-entry-alignment.md))
  would reintroduce the gap, so the two decisions have to agree.
