# O0389 — Cross-function hot-trace layout

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0106](O0106-trace-formation.md), [O0364](O0364-hot-path-block-chaining.md), [O0363](O0363-interprocedural-block-placement.md) |

## The idea

Construct long, mostly branch-free traces **across several procedures** — the
sequence of blocks a typical execution actually walks, laid out contiguously
regardless of which procedure each block came from.

Where [O0106](O0106-trace-formation.md) forms traces to give the *scheduler* a
longer straight line, this forms them to give the *fetch unit* one.

## What it needs

- Interprocedural block placement
  ([O0363](O0363-interprocedural-block-placement.md)) and block-level edge
  counts.
- Inlining decisions taken **with** the trace in mind
  ([O0401](O0401-layout-aware-inlining.md)): a trace that crosses a call
  boundary often wants that call inlined, and the two decisions are usually made
  in ignorance of each other.
- Side entries into the trace break it; duplicating them is
  [O0390](O0390-superblock-side-entry.md).
