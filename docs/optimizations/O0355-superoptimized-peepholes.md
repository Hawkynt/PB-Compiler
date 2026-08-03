# O0355 — Superoptimizer-generated peepholes

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Offline tooling → emitter |
| **Related** | [O0008](O0008-peephole-zero-idiom.md), [O0354](O0354-equality-saturation.md), [O0359](O0359-verified-arithmetic-lowering.md) |

## The idea

Search **exhaustively** (or with SMT assistance) for the shortest instruction
sequence computing a given function, and prove the replacement equivalent.
Short 8086 sequences are a small enough space to enumerate, and the results
become peephole rules — discovered rather than hand-written.

The classic outputs of this process are exactly the idioms this compiler already
uses by hand: `SBB AX,AX` for a mask, `CWD`/`XOR`/`SUB` for absolute value, the
bias-and-shift signed divide.

## What it needs

- An x86-16 **semantics model** precise enough to prove equivalence — including
  flags, which is where hand-written peepholes most often go wrong.
- A verification step per candidate rule, since an unverified superoptimizer
  result is a miscompile generator.
- Integration as a *rule table* the emitter consults, so the expensive search
  happens once, offline, not during compilation.
