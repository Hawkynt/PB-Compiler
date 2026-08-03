# O0235 — `SHLD`/`SHRD` 64-bit shifts

| | |
|---|---|
| **Status** | ✅ Implemented (constant counts 1..31) |
| **Stage** | Emitter |
| **Source** | `CodeGen` — the QUAD shift path |
| **Gate** | `--optimize` + `$CPU 80386` |
| **Verified by** | `tests/diff/DIFF73.BAS` |
| **Split from** | [C0001](C0001-386-codegen.md) |

## What it is

A 64-bit `SHIFT LEFT`/`SHIFT RIGHT` by a compile-time-constant count of 1..31
collapses the per-bit loop into `SHLD`/`SHRD` across the dword halves (EAX/EDX
only).

## Sample

```basic
$CPU 80386
DIM q AS QUAD
SHIFT LEFT q, 8
```

## Without / with

```asm
    mov     cx, 0008h        ; without: a per-bit RCL loop
Top:
    shl     ...
    rcl     ...
    loop    Top

    mov     eax, [q]         ; with
    mov     edx, [q+4]
    shld    edx, eax, 8
    shl     eax, 8
```

## Why it is safe

The count is restricted to 1..31 because the 386 **masks shift counts to five
bits** — a count of 32 or more would not do what the source says. Counts ≥ 32
and runtime counts stay on the loop, and rotates keep their loop as well.
