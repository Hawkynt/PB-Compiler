# O0296 — String move instead of copy

| | |
|---|---|
| **Status** | ✅ Implemented for private dynamic-string variables with a proven final value use |
| **Stage** | Mid-end, immediately before `mem2reg` |
| **Source** | `Ir/Passes/StringMove.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `StringMoveTests` |
| **Related** | [O0291](O0291-handle-ownership-elision.md), [O0293](O0293-copy-on-write-elision.md), [O0207](O0207-self-concat-handle-reuse.md) |

## The idea

When the source of an assignment is a **value about to be destroyed**, duplicating
its dynamic-string block is pure waste: transfer the existing handle instead.
Intrinsic results assigned directly to variables already have owned-result
semantics; O0296 handles the missing variable-to-variable case.

## Implemented IR form

Before SSA promotion, lowering exposes a dynamic-string assignment as roughly:

```text
value = load source
copy  = rt_str_dup(value)
store copy, target
...
dead  = load source
rt_str_free(dead)
```

`StringMove` rewrites that to:

```text
value = load source
store value, target
store null, source
...
dead  = load source
rt_str_free(dead)             ; frees null, so the moved block is not freed twice
```

The source clear deliberately keeps the existing destructor/reassignment control
flow intact. `mem2reg` then promotes the ordinary loads/stores and removes the
now-redundant memory traffic.

## Legality proof

A move is accepted only when all of the following are true:

- the source is a scalar BASIC dynamic-string slot whose address never escapes;
  every use must be a direct, storage-compatible load/store;
- the `rt_str_dup` result is used by exactly one target store, and the target is
  not the source slot itself;
- from that store onward, **every reachable path** reaches the source's normal
  `rt_str_free(load source)` destructor before another read or write of the
  source slot;
- a live-state loop, early normal exit, source read, source overwrite before its
  destructor, BYREF/address escape, array/global-style addressing, error-handler
  function, or inline assembly makes the pass decline.

That proof is deliberately stronger than a syntactic "next statement" check. It
handles branch diamonds where every arm destroys the old source value, while
refusing a branch where even one arm still observes it.

This is the same ownership principle as a consuming/move operation: transfer the
current value only after proving the old binding cannot be used again before its
lifetime ends. Swift SE-0366 was consulted as a behavioral reference for
flow-sensitive consume/reinitialization semantics; the PB-Compiler implementation
is independent and uses only this repository's IR and ownership conventions.

## Applies to

```basic
DIM a$, b$
b$ = a$                      ' if a$ is never read again, this is a move
' ... a$ unused from here
```

It also remains valid when `a$` is reassigned later: ordinary lowering first
frees the old value, so O0296 can move that old handle to `b$`, clear `a$`, and
let the later assignment install the new value normally.

## Deliberate boundaries

O0296 does not move out of storage that may be observed through another address.
BYREF parameters, shared/global storage, array elements and other escaped slots
therefore remain copies. Expression/intrinsic results do not need this rewrite in
the first place because their owned handles are already adopted directly.
