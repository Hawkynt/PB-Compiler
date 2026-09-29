# O0239 — `REP STOSD` array zero-fill

| | |
|---|---|
| **Status** | 🟡 Partial — HUGE and EMS array zero-fill and the BSS clear widen to `REP STOSD`; a static-array `ERASE` and a conventional dynamic-array allocation still fill bytewise |
| **Stage** | Runtime |
| **Source** | `Runtime/DosRuntime.Core.cs` — `EmitRepStoswZeroWidened`, used by `rt_hugezero` and the EMS zero-fill in `Runtime/DosRuntime.Ems.cs` and by the entry stub's BSS clear |
| **Gate** | `--optimize` + `$CPU 80386` |
| **Verified by** | `tests/diff/DIFF74.BAS` |
| **Split from** | [C0001](C0001-386-codegen.md) |

## What it is

`ERASE` on a static array — and the zero-fill an array allocation performs —
moves DWORDs instead of words, with the odd tail handled explicitly. About 4× on
a large array.

On the IR path only part of this holds. `EmitRepStoswZeroWidened` switches to
`XOR EAX,EAX` + `REP STOSD` with a trailing `STOSW` when the optimizer is on and
the target has 32-bit registers, and it serves `rt_hugezero`, the EMS array
zero-fill and the BSS clear. `IrLowering` turns `ERASE` of a static array into an
`llvm.memset`, which the x86 back end calls as `rt_memset` (`REP STOSB`) unless
`MemoryRoutineSpecialization` expands a small constant size inline, and
`rt_arr_alloc` zero-fills with `REP STOSB`. The sample below therefore still
fills byte by byte.

## Sample

```basic
$CPU 80386
DIM big%(0 TO 9999)
ERASE big%()
```

## Without / with

```asm
    mov     cx, 2710h        ; without: 10 000 word stores
    xor     ax, ax
    rep     stosw

    mov     ecx, 00001388h   ; with: 5 000 dword stores
    xor     eax, eax
    rep     stosd
```

## Why it is safe

The bytes written are identical — only the transfer width changes — and the tail
is written at the narrower width when the size is not a multiple of four.

## See also

Not zeroing at all, when an initializing loop dominates every read, is
[O0068](O0068-array-zero-fill-elision.md).
