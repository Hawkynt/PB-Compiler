# O0227 — Constant array fill → `REP STOSW`

| | |
|---|---|
| **Status** | ⬜ Not implemented on the IR path |
| **Stage** | — |
| **Source** | None for word or wider elements. The byte-wide analogue is `Ir/Passes/LibraryCallRecognition.cs` (O0330), which turns a counted one-byte-per-iteration fill into `llvm.memset` → `rt_memset` (`REP STOSB`) |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Split from** | [O0020](O0020-idiom-replacement.md) (which is now the empty-loop closed form) |

## What it is

A constant-trip `FOR` loop whose body stores a **constant** into an array
element indexed by the bare counter is a block fill, and the 8086 has an
instruction for that.

Not implemented on the IR path; the syntax-level version (`TryEmitForIdiom`) was
retired with the direct emitter. `LibraryCallRecognition` only matches a store of
one byte per iteration, so an `INTEGER` fill like the sample below stays a loop.

## Sample

```basic
$OPTIMIZE SPEED
DIM a%(0 TO 99), i%
FOR i% = 0 TO 99
  a%(i%) = 7
NEXT
```

## Without / with

```asm
    ; without: 100 iterations of compare, index, scale, store, increment

    push    ds               ; with
    pop     es
    lea     di, [a]
    mov     cx, 0064h
    mov     ax, 0007h
    rep     stosw
    mov     word ptr [i], 0064h   ; the counter's end value
```

Under `$CPU 80386` the same fill widens to `REP STOSD` with the 16-bit value
broadcast into both halves of EAX ([C0001](C0001-386-codegen.md)).

## Why it is safe

The iterates are simulated exactly like the generic loop engine, the element
range is checked against the array's static bounds, and the counter is left on
its increment-then-test end value. `$OPTIMIZE SPEED` gating keeps timing loops
intact.
