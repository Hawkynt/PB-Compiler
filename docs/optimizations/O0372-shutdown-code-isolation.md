# O0372 — Shutdown code isolation

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0370](O0370-startup-code-clustering.md), [O0367](O0367-exception-handler-outlining.md), [P0001](P0001-runtime-trimming.md) |

## The idea

Termination and cleanup — closing files, restoring the video mode, freeing the
heap, the `END`/`SYSTEM` path and the runtime's exit sequence — run **once, at
the end**. They belong on cold pages that never pollute normal execution.

For a trimmed image ([P0001](P0001-runtime-trimming.md)) this also groups the
runtime's own teardown with the program's, which is the natural pairing.

## What it needs

- The phase signal ([O0370](O0370-startup-code-clustering.md)) — and the
  structural shortcut that any block reachable only from `END`, `SYSTEM` or the
  runtime's exit is by definition shutdown code.
- Placeable fragments, including for the **runtime**, which today is emitted as
  whole sections ([P0001](P0001-runtime-trimming.md)) rather than as blocks.
