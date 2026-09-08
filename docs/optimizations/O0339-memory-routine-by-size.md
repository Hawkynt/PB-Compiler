# O0339 — Memory routine specialization by size

| | |
|---|---|
| **Status** | 🟡 Partial — small nonvolatile constant-size `memcpy`/`memset` calls use move-count-aware word/byte expansion; larger transfers retain target/runtime widening |
| **Stage** | Mid-end + emitter/runtime |
| **Source** | `Ir/Passes/MemoryRoutineSpecialization.cs`, existing widened memory runtime |
| **Gate** | `--optimize` |
| **Verified by** | `LibraryAndMemoryIdiomTests`, `BackendRuntimeCallTests`, `OptimizerTests` |
| **Related** | [O0330](O0330-library-call-recognition.md), [O0242](O0242-movsd-block-copy.md), [O0174](O0174-target-cost-models.md) |

## The idea

One copy routine is wrong for every size. Tiny known transfers can be cheaper as
straight-line accesses, medium transfers benefit from widened REP/block moves,
and large or unknown transfers belong in the runtime.

## Implemented v2

`MemoryRoutineSpecialization` runs after the ordinary middle-end has had its last
chance to scalarize or delete an aggregate copy. It expands only nonvolatile,
constant-size LLVM `memcpy`/`memset` calls and keeps far-pointer operations in the
runtime.

The profitability boundary is now expressed in **scalar accesses rather than raw
bytes**, following LLVM's `MaxStoresPerMemcpy` / `MaxStoresPerMemset` model: use
the widest legal access first, then smaller tail accesses, and refuse the inline
form once its access budget is exhausted. The current production caller is the
x86-16 late backend, so the widest universally legal access is a 16-bit word;
x86 permits that access even when the address is not naturally aligned.

- `memcpy` allows three scalar loads/stores. That covers 0..6 bytes as word-first
  `2/2/2` plus a possible byte tail. Every load is immediately followed by its
  store, so only one copied scalar is live at a time.
- Constant-byte `memset` allows four stores and splats the byte into compile-time
  word constants, covering 0..8 bytes without runtime arithmetic.
- Dynamic-byte `memset` keeps byte stores; manufacturing a repeated word from a
  runtime byte would add arithmetic/register pressure, particularly badly on an
  8086. Its four-store budget therefore covers 0..4 bytes.
- Seven- and eight-byte `memcpy` deliberately remain intrinsics. That preserves
  the existing `$CPU 80386` runtime path that consumes a DWORD with `REP MOVSD`
  and then copies the tail instead of replacing it with four scalar word/byte
  transfers.

The threshold shape is based on LLVM's documented target-lowering contract, not
on copied implementation code:

- <https://llvm.org/docs/doxygen/TargetLowering_8h_source.html>
- <https://llvm.org/docs/LangRef.html#volatile-memory-accesses>

LLVM is Apache-2.0 WITH LLVM-exception; O0339 is independently implemented here.

## Applies to

```basic
TYPE Point
  x AS INTEGER
  y AS INTEGER
END TYPE
DIM a AS Point, b AS Point
b = a                        ' 4 bytes: two straight-line word moves are eligible
```

## Still planned

- Per-target access budgets instead of the conservative x86-16 baseline budget.
- DWORD inline moves when `$CPU 80386` makes a real 32-bit scalar move available
  in the routed selector, with alignment/register-pressure-aware profitability.
- Target-specific handling for additional known medium sizes.
- Explicit profitability coordination with SROA and aggregate scalarization.
