# O0057 — Storage narrowing

| | |
|---|---|
| **Status** | ⬜ Planned (the analysis half exists — [O0016](O0016-value-fact-analysis.md) already proves the width) |
| **Stage** | Shared analysis; the *decision* belongs to each back end |
| **Related** | [O0016](O0016-value-fact-analysis.md), [O0012](O0012-float-demotion.md), [C0001](C0001-386-codegen.md) |

## The idea

A value whose facts prove it fits a narrower type is **stored** as one,
converting only at the boundaries: a `LONG` whose every reaching value lies in
0..255 becomes a `BYTE` slot, an `EXT`/`DOUBLE` whose precision is provably
unused collapses to `SINGLE`. That shrinks data slots, frames and spill traffic,
and cascades into float demotion and fixed-point.

## Applies to

```basic
DIM count&, i&
FOR i& = 0 TO 200
  count& = count& + 1        ' never leaves 0..201
NEXT
```

## Today

`count&` and `i&` occupy 4 bytes each and every operation is a DX:AX pair op.

## Planned

Both become 2-byte cells (or 1-byte on a target where byte ops pay), with a
widening conversion only where the value is observed as a `LONG`.

## Where the decision belongs

Narrowing is a fact the **optimizer** proves; the machine width stays the
**emitter's** choice. On x86-16 that choice is deliberately "don't": a byte slot
saves one byte of data and no cycles, because the 8086's byte ALU is no faster
than its word ALU and partial-register access costs more than it saves. The same
fact on a target with cheap byte operations — or on the C back end, where a
narrower type is free — should be taken up. That is exactly why it belongs in
the shared analysis rather than in the x86 emitter.

## What it needs

- The facts are already available; what is missing is a representation change in
  the symbol/slot layer plus conversion insertion at the boundaries.
- **Caution — PB wraps silently.** Narrowing must prove that *intermediates*,
  not just stored values, stay in range under the original type's wrap behavior,
  otherwise a 16-bit wrap appears where 32-bit code wrapped differently, which is
  observable. Under `$ERROR NUMERIC ON` the proof must additionally preserve
  *which* statement overflows first.
