# O0268 — Profile collection and representation

| | |
|---|---|
| **Status** | ⬜ Planned — **the prerequisite for every profile-guided entry** |
| **Stage** | Compiler + runtime infrastructure |
| **Related** | [O0269](O0269-profile-guided-inlining.md), [O0274](O0274-profile-guided-code-layout.md), [O0404](O0404-stale-profile-matching.md) |

## The idea

Every profile-guided optimization needs the same two things: a way to **produce**
execution counts, and a stable way to **attach** them to compiler objects.

Two production modes, as in every mature toolchain:

- **instrumented** — the compiler emits counters at block entries and call
  edges, the program writes them out at exit;
- **sampled** — a timer or hardware-counter interrupt records the instruction
  pointer, which is cheaper and unbiased but coarser. On DOS this means hooking
  `INT 08h` or a PIT-driven handler.

The representation is the harder half: counts must attach to **stable
identities** — procedure name plus a structural block ID — so a profile survives
recompilation and small source edits
([O0404](O0404-stale-profile-matching.md)).

## What it needs

- A `--profile-generate` / `--profile-use` pair on the CLI, and a profile file
  format.
- Block and edge IDs assigned during code generation and preserved through every
  later pass — which is also what the layout family needs
  ([O0360](O0360-basic-block-fragments.md)).
- A default when no profile exists: the static heuristics of
  [O0104](O0104-block-placement.md), so the pipeline is the same either way.
