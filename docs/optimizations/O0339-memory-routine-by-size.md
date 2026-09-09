# O0339 — Memory routine specialization by size

| | |
|---|---|
| **Status** | 🟡 Partial — per-CPU scalar budgets and 386+ DWORD scalar lowering are implemented; larger/unknown transfers intentionally retain runtime/block-move selection |
| **Stage** | Mid-end + x86 machine combine + emitter/runtime |
| **Source** | `Ir/Passes/MemoryRoutineSpecialization.cs`, `CodeGen/TargetCost.cs`, `Backend/MachineCombiner.cs`, existing widened memory runtime |
| **Gate** | `--optimize`; DWORD accesses additionally require `$CPU 80386` or later |
| **Verified by** | `LibraryAndMemoryIdiomTests`, `O0339MemoryRoutineSpecializationTests`, `TargetCostTests`, `BackendRuntimeCallTests`, `OptimizerTests` |
| **Related** | [O0330](O0330-library-call-recognition.md), [O0242](O0242-movsd-block-copy.md), [O0174](O0174-target-cost-models.md) |

## The idea

One copy routine is wrong for every size. Tiny known transfers can be cheaper as
straight-line accesses, medium transfers benefit from widened REP/block moves,
and large or unknown transfers belong in the runtime.

## Implemented v3

`MemoryRoutineSpecialization` runs after the ordinary middle-end has had its last
chance to scalarize or delete an aggregate copy. It expands only nonvolatile,
constant-size LLVM `memcpy`/`memset` calls and keeps far-pointer operations in the
runtime.

The profitability boundary is expressed in **scalar stores rather than raw
bytes**, following LLVM's `MaxStoresPerMemcpy` / `MaxStoresPerMemset` target-lowering
contract: consume the widest legal scalar first, use smaller accesses for the
tail, and refuse the inline form once the target's store budget is exhausted.
The targetless overload deliberately retains the conservative 8086 policy so a
standalone IR consumer never silently assumes a newer CPU.

`TargetCost` currently supplies this policy:

| CPU tier | scalar width | `memcpy` stores | constant `memset` stores |
|---|---:|---:|---:|
| 8086 | 16 bit | 3 | 4 |
| 80286 | 16 bit | 4 | 6 |
| 80386 | 32 bit | 4 | 8 |
| 80486 | 32 bit | 6 | 12 |
| Pentium / P6 | 32 bit | 8 | 16 |

`$OPTIMIZE SIZE` clamps the later targets to four copy stores and eight constant
fill stores. It does **not** reduce scalar width: an i386 remains capable of a
DWORD move even when bytes are the objective.

### Copy plans

The plan is widest-first. Examples:

- six bytes on an 8086: `2 + 2 + 2`;
- eight bytes on a 286: `2 + 2 + 2 + 2`;
- seven bytes on a 386: `4 + 2 + 1`;
- sixteen bytes on a 386: `4 + 4 + 4 + 4`; seventeen bytes exceeds that tier's
  four-store budget and remains a memory intrinsic;
- twenty bytes fits the P6 SPEED budget but exceeds the P6 SIZE clamp.

Every `memcpy` load remains immediately paired with its store at IR level, so at
most one copied scalar is live at a time. The machine combiner then recognizes
the selector's exact private word-pair form of an i32 load/store and rejoins it
into one DWORD load plus one DWORD store on optimized 386+ targets. Both word
halves must have exactly one definition and one use, and the two memory operands
must be adjacent views of the same cell; ordinary LONG values therefore keep the
backend's baseline word-pair representation and ABI.

### Fill plans

A constant byte is splatted at compile time into the target's widest scalar:
`5Ah` becomes `5A5Ah` on the 8086/286 and `5A5A5A5Ah` on the 386+. A 32-byte
constant fill therefore fits exactly in the 386's eight-store budget; 33 bytes
does not.

A dynamic byte deliberately stays byte-wise with a fixed four-store ceiling on
every tier. Building a repeated word or dword from a runtime byte would add
arithmetic and register pressure just to make the store wider.

When a 386 constant fill reaches selection outside the SPEED residency policy,
an i32 constant is represented as two adjacent word-immediate stores. The target
machine combiner rejoins only matching repeated-word pairs into one DWORD
immediate store, so DWORD **legality** follows `$CPU`, not the separate policy for
keeping general LONG values resident in 32-bit registers.

## References and licensing

The threshold shape is based on LLVM's documented target-lowering contract, not
on copied implementation code:

- LLVM `TargetLowering.h`, `MaxStoresPerMemcpy` / `MaxStoresPerMemset`:
  <https://llvm.org/docs/doxygen/TargetLowering_8h_source.html>
- LLVM Language Reference, volatile memory accesses:
  <https://llvm.org/docs/LangRef.html#volatile-memory-accesses>
- Intel 80386 Programmer's Reference Manual, `MOV` (`r32,r/m32` and `r/m32,r32`)
  for the native DWORD forms.

LLVM is Apache-2.0 WITH LLVM-exception. The Intel manual is used as the ISA
specification. O0339 is independently implemented; no external implementation
code was copied or translated.

## Applies to

```basic
TYPE Point
  x AS INTEGER
  y AS INTEGER
END TYPE
DIM a AS Point, b AS Point
b = a                        ' 4 bytes: straight-line scalar moves are eligible
```

On `$CPU 80386` and later, a qualifying four-byte scalar transfer can become one
native DWORD load/store pair; the same source on an 8086/286 remains word-based.

## Still planned

- More measured target-specific tuning for medium known sizes versus REP/runtime
  forms; the current budgets are intentionally bounded and monotonic.
- Alignment-sensitive profitability if a future target makes unaligned scalar
  access materially different. Real-mode x86 itself permits the current accesses.
- Explicit profitability coordination with SROA and aggregate scalarization beyond
  the existing ordering rule that gives scalar replacement the first opportunity.
