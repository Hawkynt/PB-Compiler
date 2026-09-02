# O0355 — Superoptimizer-generated peepholes

| | |
|---|---|
| **Status** | ✅ Implemented (bounded exhaustive search) |
| **Stage** | After instruction selection |
| **Related** | [O0008](O0008-peephole-zero-idiom.md), [O0354](O0354-equality-saturation.md), [O0359](O0359-verified-arithmetic-lowering.md) |

## The idea

Search **exhaustively** (or with SMT assistance) for the shortest instruction
sequence computing a given function, prove the replacement equivalent, and make
compilation itself consume only the proven catalog.

PB-Compiler implements a deliberately small x86-16 superoptimizer in
`Backend/SuperoptimizedPeepholes.cs`. It enumerates a one-register candidate
instruction vocabulary, compares each candidate against each supported source
pattern for all **65 536** word inputs, and retains only strictly smaller
encodings. The resulting dictionary is built once; matching the selected machine
stream is then cheap.

Current searched source patterns include `ADD r,1`, `SUB r,1`, `XOR r,-1`,
`AND r,0`, and `ADD r,r`. Candidate instructions include `INC`, `DEC`, `NOT`,
`SHL r,1`, and `XOR r,r`. A rule such as `ADD r,r -> SHL r,1` is therefore not
accepted merely because it is equivalent: on the 8086 generic-register byte-cost
model it is not smaller.

## Safety and limits

- Value semantics are exhaustively proven over the complete 16-bit domain.
- The current search models value results, not every EFLAGS bit. Therefore a
  discovered replacement is used only where later machine instructions prove
  the source flags are dead. Reaching a block boundary is not such a proof.
- The search space is intentionally bounded to short scalar word idioms. It is
  infrastructure for growing a generated catalog, not a claim of general x86
  superoptimization.
- No SMT solver or third-party package is required at compile time.
