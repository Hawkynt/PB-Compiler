# R0001 — Fast text output

| | |
|---|---|
| **Status** | ✅ Implemented (`$OPTION VIDEO`) |
| **Stage** | Runtime + emitter |
| **Source** | `Runtime/DosRuntime.Print.cs` (the B800h writer), `Runtime/DosRuntime.Internals.cs`, `CodeGen/CodeGenerator.cs`, `Semantics/SemanticModel.cs` |
| **Gate** | `$OPTION VIDEO` |
| **Verified by** | the screen-capture oracle (2026-07) — a BASIC helper PEEKs B800 text memory into `SCREEN.TXT` after an unredirected run |
| **Related** | [R0002](R0002-fast-graphics.md), [P0007](P0007-trivial-io-lowering.md) |

## What it is

`PRINT` normally goes through DOS (`INT 21h`), one character or one run at a
time — slow, and *also* a documented divergence from genuine PowerBASIC, which
prints straight to video memory (see `docs/QUIRKS.md`).

With `$OPTION VIDEO`, printable in-line runs are written directly into the
B800h text buffer with attribute bytes, followed by **one** BIOS cursor resync
per statement. Control characters, wraps, files and `STDOUT` keep the exact DOS
path, so redirected output is unchanged.

## Sample

```basic
$OPTION VIDEO
DIM i%
FOR i% = 1 TO 25
  PRINT "row"; i%
NEXT
```

## Without the option

```asm
    mov     ah, 40h          ; per run
    mov     bx, 1
    int     21h
    ...                      ; DOS char-by-char processing, per statement
```

## With the option

```asm
    push    0B800h           ; the text page
    pop     es
    mov     di, [rt_cursor]
    rep     movsw            ; character + attribute pairs
    ...                      ; one BIOS cursor resync per statement
```

## Equivalent BASIC

```basic
DEF SEG = &HB800
FOR j% = 1 TO LEN(t$) : POKE offset, ASC(MID$(t$, j%, 1)) : NEXT
```

## Why it is safe

The screen-capture oracle compares the **observable screen** across build
variants, not the byte stream: captures are screen-identical either way. Control
characters, line wraps and any file-directed output stay on the DOS path
precisely because their side effects (scrolling, redirection, `^Z` handling) are
what the DOS layer implements.
