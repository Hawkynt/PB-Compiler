# O0273 — Profile-guided register allocation

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Register allocation |
| **Related** | [O0058](O0058-386-register-allocation.md), [O0176](O0176-register-pressure-scheduling.md), [O0268](O0268-profile-collection.md) |

## The idea

Spill cost is not uniform: a reload inside a loop that runs a million times costs
a million memory accesses, and one on an error path costs one. Weighting each
live range by its **block execution frequency** puts the registers where the
program actually spends its time — and places the spills where it does not.

## Applies to

```basic
SUB Render
  LOCAL x%, y%, err%
  FOR y% = 0 TO 199          ' hot: x% and y% deserve the registers
    ...
  NEXT
  IF err% THEN ...           ' cold: err% is a fine spill candidate
END SUB
```

## Implementation

The machine IR exposes an optional execution count on each `MBlock`. When every
block of a function has a measured count, the spiller computes one cost per
virtual register by summing the execution count for every machine-level read and
write of that value. A directly spilled register reference becomes a memory
reference on this x86 backend, so this is the expected dynamic memory traffic the
spill creates. Read/modify/write operands count both the read and the write.

The cost is used in both places where the allocator chooses *which* value to move:

- direct spills choose the lowest profile cost first, then retain the old
  parameter-cell, live-range-length, and virtual-id tie-breaks;
- explicit live-range splitting keeps the termination-critical "untouched before
  already moved" rule first, then chooses the lowest profile cost before the old
  longest-range tie-break.

Counts use saturating unsigned arithmetic, so even a pathological profile cannot
wrap a very hot value around to a deceptively cheap cost. The allocator requires a
**complete** profile: if any machine block has no execution count, O0273 declines
to influence ordering and the previous structural policy is used unchanged. In
particular, missing profile data is never interpreted as a zero-frequency block.

`MFunction.Clone()` preserves execution counts, which matters for the speculative
`$OPTIMIZE SPEED` allocation path: profile information survives the allocator's
copy/try/adopt cycle.

## Profile collection

[O0268](O0268-profile-collection.md) remains the producer-side work: it must map
persisted counters onto all selected machine blocks before allocation. O0273 is
the consumer and is independently testable by attaching counts directly. Until a
profile is attached, the existing loop-residency heuristic remains the static
"inside a loop is hotter" approximation and spill ordering remains exactly as it
was.

## Reference model and licensing

The design was checked against LLVM's register allocator and spill-weight logic:
LLVM derives register spill weights from block-frequency-weighted uses/definitions
and prefers spilling lower-weight interferences. LLVM is Apache-2.0 WITH
LLVM-exception. PB-Compiler does not copy or translate that implementation; the
code here independently implements the target-specific rule above using the
existing machine IR and spiller.
