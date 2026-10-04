# O0408 — Frame slot sharing (6502, x86-32, x64)

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | 6502 and x86-32/x64 back ends, frame layout |
| **Source** | `Backend/ValueLiveRanges.cs`; `Backend/Mos6502/Mos6502Compiler.Module.cs` (`LayOut`), `Mos6502Compiler.Function.cs` (`Edge`, `Copy`); `Backend/X86Native/X86NativeCompiler.Module.cs` (`LayOut`) |
| **Gate** | always, on `--platform 6502`, `x86-32` and `x64` |
| **Verified by** | `Mos6502CodeSizeTests`, `Mos6502BatteryTests`, `NativeBatteryTests`, `FlatTargetIdiomTests` (each flat target against the DOS build) |
| **Related** | [O0409](O0409-6502-accumulator-load-elimination.md), [O0410](O0410-6502-branch-shaping.md), [O0005](O0005-register-residency.md) |

## What it is

The 6502 has three registers and no stack worth the name, so every SSA value lives in memory: a
slot in its function's frame, and frames overlay one another along the call graph. A slot used to
belong to its value for the whole function, so a frame was the sum of everything the function ever
computed. Now each value's life is measured - from the instruction that writes it to the last that
reads it, widened to every block it is live into or out of, a phi written at the end of each
predecessor - and values whose lives never meet share a slot. A frame is as big as the most that is
alive at once.

Two copies go with it. A cast that keeps a value's low bytes - a truncation, a pointer read as a
number - takes its source's cell instead of a copy of it, the source's life stretched to cover the
cast's; and the copies into a block's phis on an edge go straight from source to phi, in an order in
which no phi is overwritten while another still has to read it, parking a value in the staging area
only to break a cycle - where every phi used to be staged and copied twice whenever one read another.

x86-32 and x64 keep every SSA value in a frame cell below the frame pointer too, and lay their
frames out the same way from the same live ranges (`ValueLiveRanges`), each cell aligned to its size:
a smaller frame there is a shallower stack - recursion goes deeper before `$ERROR STACK` stops it -
and fewer cache lines.

On the 6502 that matters twice over. Less RAM, and more of the frames fit in the page-zero window (`$02`-`$8F`),
where `LDA`/`STA` take two bytes instead of three. A value and the operands of the instruction that
writes it always meet, so a result never lands on what it is computed from; a division's partner,
which the division writes, starts its life there; and a function with an `ON ERROR` handler, which
can resume anywhere, keeps one slot per value.

## Sample

```basic
t& = 1
FOR i% = 1 TO 16
  t& = t& * 3 + INP(i%) + i%
NEXT
PRINT t&
```

## Without it

Each of the loop's temporaries - the product, the port read widened, the sums - holds a cell of its
own for the whole function, so the frame grows with every one and its later cells land past the
page-zero window, reached with three-byte absolute instructions.

## With it

A temporary that is dead before the next is written hands its cell on, so the loop's temporaries
reuse a few cells - in page zero, where the frame now fits - and each access is a byte shorter.

`tests/idioms/FILES3.BAS` went from 44 052 bytes to 41 345 with this alone, and fits a C64 with the
two passes beside it.

## Equivalent BASIC

None - the program is unchanged; only where the compiled code keeps its intermediate results is.
