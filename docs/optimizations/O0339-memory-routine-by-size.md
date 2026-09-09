# O0339 — Memory routine specialization by size

| | |
|---|---|
| **Status** | 🟡 Partial — nonvolatile constant `memcpy`/`memset` up to 4 bytes is expanded in the IR; existing target/runtime widening covers larger known copies |
| **Stage** | Mid-end + emitter/runtime |
| **Source** | `Ir/Passes/MemoryRoutineSpecialization.cs`, existing widened memory runtime |
| **Gate** | `--optimize` |
| **Verified by** | `LibraryAndMemoryIdiomTests`, `BackendRuntimeCallTests`, `OptimizerTests` |
| **Related** | [O0330](O0330-library-call-recognition.md), [O0242](O0242-movsd-block-copy.md), [O0174](O0174-target-cost-models.md) |

## The idea

One copy routine is wrong for every size. Tiny known transfers can be cheaper as
straight-line accesses, medium transfers benefit from widened REP/block moves,
and large or unknown transfers belong in the runtime.

## Implemented v1

`MemoryRoutineSpecialization` expands nonvolatile constant-size LLVM
`memcpy`/`memset` calls of 0..4 bytes into byte loads/stores in the IR. The
four-byte ceiling is intentionally conservative and target-neutral: it captures
the motivating two-word record copy without stealing 7/8-byte UDT copies from
the existing `$CPU 80386` target-aware `REP MOVSD` path.

Larger/unknown transfers stay as intrinsics and continue through the existing
runtime ABI. The adjacent local-array SROA proof also requires accesses to match
the element storage width, preventing byte-expanded packed storage from being
misread as wider scalar elements.

## Applies to

```basic
TYPE Point
  x AS INTEGER
  y AS INTEGER
END TYPE
DIM a AS Point, b AS Point
b = a                        ' 4 bytes: straight-line copy is eligible
```

## Still planned

- Per-target thresholds instead of the conservative four-byte mid-end ceiling.
- Word/DWORD inline moves selected with alignment and register-pressure
  awareness rather than byte accesses in target-neutral IR.
- Target-specific handling for additional known medium sizes.
- Explicit profitability coordination with SROA and aggregate scalarization.
