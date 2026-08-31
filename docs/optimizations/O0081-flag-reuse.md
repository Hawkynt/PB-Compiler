# O0081 — Flag reuse and `TEST` instead of `CMP …,0`

| | |
|---|---|
| **Status** | ✅ Done — `CMP reg,0 → TEST reg,reg`, bit-test conditions, and late reuse of ZF/SF/PF from a preceding result-producing ALU instruction all ship |
| **Stage** | Assembler peephole + codegen |
| **Related** | [O0008](O0008-peephole-zero-idiom.md), [O0031](O0031-branch-fusion.md), [O0038](O0038-instruction-scheduling.md) |

## The idea

Two related rewrites:

1. `CMP r,0` → `TEST r,r` (or the existing `OR r,r`) — two bytes instead of
   three or four, same ZF/SF.
2. **Redundant compare elimination**: `ADD`, `SUB`, `AND`, `OR`, `XOR`, `ADC`,
   `SBB`, `INC`, `DEC`, `NEG` and fixed-count `SHL`/`SHR`/`SAR` already set
   ZF/SF/PF from their result. A zero test of that unchanged result is redundant
   when the following branch consumes only one of those flags.

## Applies to

```basic
DIM n%
n% = n% - 1
IF n% = 0 THEN PRINT "done"
```

## Before

```asm
    mov     ax, [n]
    dec     ax
    mov     [n], ax
    mov     ax, [n]
    or      ax, ax           ; the flags DEC already set
    jnz     Skip
```

## Now — bit-test conditions

`IF x AND mask THEN …` is a bit test whose truth is only the AND's zero-ness, so the
code generator emits it as one `TEST` rather than materializing the masked value and
testing it separately:

```asm
    mov     ax, [x]
    test    ax, mask         ; ZF = (x AND mask) == 0 - no `and ax,mask` + `test ax,ax`
    jz      Else
```

Runtime-identical to `and ax,mask; test ax,ax` (the branch reads the same ZF; the AND
result is never stored), and it leaves `AX` holding `x` unmodified. Applies to an
int16 `AND` whose other operand folds to a constant and whose value is not also wanted
for CSE; recognized in `EmitConditionalBranch` before the comparison-fusion path, so it
survives into the `$OPTIMIZE SPEED` scheduler's stream (unlike the older peephole below).

The explicit forms `(x AND mask) = 0` and `(x AND mask) <> 0` are the same bit test —
comparing against zero asks exactly what `TEST` already answers — so they take the same
single instruction and differ only in which way the branch goes:

```asm
    mov     ax, [x]
    test    ax, mask
    jnz     Else             ; `= 0` falls through when the bit is clear
    ; ... or `jz Else` for `<> 0`
```

The comparison is recognized with either operand order (`0 = (x AND mask)` too), and the
zero side is whatever the constant folder reduces to zero, not just a literal `0`.

Both forms back off when the `AND` is CSE'd: a second use of the same `x AND mask` needs
the value, so it is materialized normally.

## Now — result flags survive flag-neutral work

The assembler's recorded def/use stream runs after expression lowering, CSE, inlining and
load forwarding. That makes the common store/reload shape visible as exactly what a human
would write:

```asm
    dec     ax
    mov     [n], ax          ; MOV does not touch flags
    jnz     Skip             ; DEC's ZF is still the answer
```

`RunLoadForwarding` first removes the redundant `mov ax,[n]`; its O0081 follow-up then
removes a word `CMP r,0`, `TEST r,r` or `OR r,r` when all of the following are proven:

- the closest preceding flag writer also writes the tested register and is one of the
  operations whose ZF/SF/PF are defined from its final result;
- every instruction between producer and test is recorded, byte-adjacent and does not
  overwrite that register or the flags;
- no label enters between producer and test, and the test itself is not an alternate entry;
- the immediately following conditional branch reads only ZF, SF or PF (`JZ/JNZ`,
  `JS/JNS`, `JP/JNP`).

The pass intentionally does **not** reuse CF/OF for a zero comparison. `SUB AX,1` and
`CMP AX,0` may produce the same ZF/SF/PF, but their carry flags mean different things.
Likewise signed ordering branches need OF in addition to SF, so they keep the explicit
comparison. Variable-count shifts are excluded because a runtime count of zero leaves
EFLAGS unchanged.

Calls, inline assembly, segment-register changes and every other unrecorded instruction
are barriers. This is conservative by construction: a missing optimization costs bytes;
a guessed flag lifetime costs correctness.
