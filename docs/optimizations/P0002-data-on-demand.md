# P0002 — Data on demand

| | |
|---|---|
| **Status** | ✅ Implemented (section-granular data) |
| **Stage** | Image assembly |
| **Source** | `Runtime/DosRuntime.*` (per-subsystem data sections), `CodeGen/RuntimeTrimmer.cs` |
| **Gate** | `--optimize` |
| **Related** | [P0001](P0001-runtime-trimming.md), [P0003](P0003-bss.md), [P0004](P0004-right-sized-memory.md) |

## What it is

Trimming the runtime's **code** is only half the job: the runtime also carries
static data — a 2 KiB string descriptor table (512 × 4), the PRINT capture
buffer, the file table, the `REG` block for `CALL INTERRUPT`, the `DATA` pool.
Those are split into per-subsystem sections and emitted only when the trimmed
runtime actually references them.

The string console cells, for example, are now their own section, separate from
the descriptor table — so a program that prints but never uses a dynamic string
pays for neither.

## Sample

```basic
PRINT "Hello, World!"
```

## Without the optimizer

```
Data
  3A78   512  rt_fieldbuf      ; FIELD buffer
  3C78     2  rt_chfh          ; file table
  3C86     2  rt_dataptr       ; DATA pool cursor
  ...
  2048 bytes  string descriptor table
```

## With the optimizer

```
Data
  (the console cell section only)
```

No descriptor table, no file table, no `REG` block, no `DATA` pool — none of
them is referenced by the three runtime routines this program keeps.

## Equivalent BASIC

Unchanged.

## Why it is safe

A data section is dropped by exactly the same reachability closure that drops a
code section ([P0001](P0001-runtime-trimming.md)): if no surviving instruction
can name the label, nothing can read or write it. Sizes that used to be
hard-coded become tunables next to `$STRING`, so a program that *does* use
strings can still choose a smaller descriptor count.
