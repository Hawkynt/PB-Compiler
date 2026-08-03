# P0001 — Runtime trimming

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Image assembly, after user code is emitted |
| **Source** | `CodeGen/RuntimeTrimmer.cs`, `Runtime/DosRuntime.BindDeferred` |
| **Gate** | `--optimize` (not the dialect) |
| **Related** | [P0002](P0002-data-on-demand.md), [P0007](P0007-trivial-io-lowering.md), [O0022](O0022-dead-procedure-elimination.md) |

## What it is

The DOS runtime is a large body of hand-written assembly — string heap, file
table, x87 formatter, array engine, error handlers. Classically **all** of it is
linked into every program. Runtime trimming emits only the sections the program
can actually reach.

A one-time **probe emission** maps every runtime label to its providing section
and every section to the labels it references. A reachability closure seeded from
the labels the user program references then selects the minimal section set,
which is emitted **after** the user code (`DosRuntime.BindDeferred` pre-binds the
label surface so forward references resolve).

Because the probe derives from the genuine emission rather than from a
hand-maintained table, the dependency graph cannot drift out of date.

## Sample

```basic
PRINT "Hello, World!"
```

## Without the optimizer

```
code : 12153 bytes
Runtime
  rt_exit, rt_print_str, rt_print_nl, rt_print_i16, rt_print_f32,
  rt_print_flt, rt_fd_digits, rt_pow, rt_lmul, rt_strcat, rt_strmem,
  rt_fieldbuf, rt_datapool, ...          ; the whole library
```

14 254 bytes on disk.

## With the optimizer

```
Runtime
  rt_print_str
  rt_print_nl
  rt_exit
```

916 bytes on disk through the general trimmed path — and 25 bytes when
[P0007](P0007-trivial-io-lowering.md)'s fast path also applies.

## Equivalent BASIC

Unchanged — the program is the same; only the library it drags along shrinks.

## Why it is safe

The closure is over the **actual** label references in the emitted image, so a
section is dropped only when nothing in the surviving code can name it. Anything
reached indirectly (an error-handler vector, a `DATA` pool, a runtime helper
another helper calls) is reached through the same graph. The probe emission
guarantees the graph is the real one.
