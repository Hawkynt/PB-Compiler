# O0089 — Sign- and zero-extension elimination

| | |
|---|---|
| **Status** | 🟡 Partial — lossless extend/truncate round trips eliminated in IR |
| **Stage** | IR mid-end + emitter |
| **Related** | [O0016](O0016-value-fact-analysis.md), [O0090](O0090-demanded-bits.md), [C0001](C0001-386-codegen.md) |

## The idea

Widening conversions are emitted structurally — a `BYTE` read zero-extends, a
`SBYTE` read sign-extends (`CBW`), a 16→32 promotion sign-extends (`CWD`) — even
when the extra width is never observed or is already known to have the required
bits. Such conversions should disappear before instruction selection whenever
the proof is target-independent.

## Implemented

The `$OPTIMIZE SPEED` demanded-bits pass removes the canonical lossless round
trip

```text
trunc (zext x to W) to sizeof(x)
trunc (sext x to W) to sizeof(x)
```

by replacing the truncation directly with `x`. Integer signedness is an
interpretation rather than a storage distinction in this IR, so the proof uses
`IrType.SameStorage`: the original bit pattern is exactly the result. DCE then
collects the unused extension.

This matters for zero-overhead wrappers and generic code that widens an input for
an intermediate API but immediately returns it through the original narrow type:
there is no residual `MOVZX`, `MOVSX`, `CBW`, `CWD`, mask or helper call merely
because the source abstraction widened internally.

## Wider target

```basic
DIM b AS BYTE, n%, t&
n% = b                      ' 0..255: the high byte is already zero
t& = n% AND &H7FFF          ' non-negative: CWD would write a zero DX
```

A fully developed pass should use known bits/ranges to avoid redundant
extensions even when there is no adjacent extend/truncate pair.

## Still planned

- Consume the **known-bits** domain from [O0016](O0016-value-fact-analysis.md) to
  prove high bytes/words already zero or already sign-filled.
- Under `$CPU 80386`, feed those facts into target selection so it can choose
  between `MOVZX`, `MOVSX` and a plain `MOV` ([C0001](C0001-386-codegen.md)).
- Fold extension through phis, selects and memory-forwarded values where all
  incoming paths establish the same high-bit fact.
- Keep extensions that are load-bearing for integral-to-float promotion or any
  other genuinely wide observation.
