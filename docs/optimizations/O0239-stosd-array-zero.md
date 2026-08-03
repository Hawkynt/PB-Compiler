# O0239 — `REP STOSD` array zero-fill

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Arrays.cs` (`ERASE` / allocation zero-fill) |
| **Gate** | `--optimize` + `$CPU 80386` |
| **Verified by** | `tests/diff/DIFF74.BAS` |
| **Split from** | [C0001](C0001-386-codegen.md) |

## What it is

`ERASE` on a static array — and the zero-fill an array allocation performs —
moves DWORDs instead of words, with the odd tail handled explicitly. About 4× on
a large array.

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
