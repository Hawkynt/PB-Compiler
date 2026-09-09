# O0296 — String move instead of copy

| | |
|---|---|
| **Status** | ⬜ Planned (intrinsic results assigned to variables already move their handle) |
| **Stage** | Emitter |
| **Related** | [O0291](O0291-handle-ownership-elision.md), [O0293](O0293-copy-on-write-elision.md), [O0207](O0207-self-concat-handle-reuse.md) |

## The idea

When the source of an assignment is a **temporary about to be destroyed**, the
copy is pure waste: transfer the handle instead. `StrAssign` already adopts an
intrinsic's result rather than copying it; the general case is any expression
whose value is dead after the assignment.

## Applies to

```basic
DIM a$, b$
b$ = a$                      ' if a$ is never read again, this is a move
' ... a$ unused from here
```

## What it needs

- **Last-use analysis** over string values — the same liveness
  [O0291](O0291-handle-ownership-elision.md) needs, since a move is exactly an
  elided dup plus an elided free.
- Certainty about aliasing: moving a handle out of a variable that something else
  can still reach (a BYREF parameter, a `SHARED` global, an array element) would
  leave a dangling handle, so the source must be provably private
  ([O0260](O0260-escape-analysis.md)).
