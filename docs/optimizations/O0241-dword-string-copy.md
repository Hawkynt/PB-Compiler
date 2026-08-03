# O0241 — DWORD-wide string copy

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter + string runtime |
| **Source** | `EmitRepMovsbWidened` |
| **Gate** | `--optimize` + `$CPU 80386` |
| **Verified by** | `tests/diff/DIFF40.BAS` (a 386 string storm) |
| **Split from** | [R0003](R0003-string-engine.md) |

## What it is

The string runtime's literal and concat copy moves **DWORDs** plus a ≤ 3-byte
tail instead of `REP MOVSB` — roughly 4× on long strings.

## Sample

```basic
$CPU 80386
DIM a$, b$
a$ = STRING$(4000, "x")
b$ = a$ + a$
```

## Without / with

```asm
    mov     cx, 0FA0h        ; without: 4 000 byte moves
    rep     movsb

    mov     ecx, 000003E8h   ; with: 1 000 dword moves
    rep     movsd
    mov     cx, <0..3>       ; plus the tail
    rep     movsb
```

## Why it is safe

The copied bytes are identical; only the transfer width changes. The tail is
computed from the length modulo four, so no byte is copied twice or missed.
