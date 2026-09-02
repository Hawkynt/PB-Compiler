# O0070 — Leaf-frame elision

| | |
|---|---|
| **Status** | 🟡 Partial — IR-routed frame-free procedures |
| **Stage** | IR middle-end analysis + routed x86 emitter |
| **Related** | [O0019](O0019-zero-elision.md), [O0021](O0021-register-parameters.md), [O0006](O0006-inlining.md), [O0170](O0170-leaf-register-save-elision.md) |

## The idea

[O0019](O0019-zero-elision.md) removes unnecessary frame *zeroing*. O0070 removes
the **frame itself** when the function has no state that needs one. After SROA
and `mem2reg`, a procedure whose fixed local state has disappeared can often keep
every value in SSA/register form; if register allocation also needs no spill
slots, the routed emitter can omit the `PUSH BP` / `MOV BP,SP` prologue and the
`MOV SP,BP` / `POP BP` epilogue.

A call does not by itself require a private frame. A non-leaf procedure remains
eligible when its own state is frame-free and the target calling sequence does
not need BP for anything persistent.

## Applies today

```basic
$OPTIMIZE SPEED
FUNCTION Answer%() NOINLINE
  LOCAL x%
  x% = 6 * 7
  Answer% = x%
END FUNCTION
```

After scalar promotion the local has no storage identity, so the IR contains no
`alloca`. If selection and register allocation introduce no spill slot, the
routed x86-16 emitter can produce the equivalent of:

```asm
Answer:
    mov     ax, 42
    ret
```

instead of:

```asm
Answer:
    push    bp
    mov     bp, sp
    mov     ax, 42
    mov     sp, bp
    pop     bp
    ret
```

## Two-stage proof

The implementation deliberately splits the decision:

1. **IR middle end:** `FrameElision.IsCandidate` runs after the normal optimizer
   has had the chance to remove scalar/aggregate stack storage. A surviving
   `alloca`, error-handler state, or inline assembly makes the function
   ineligible. Calls do not.
2. **Final machine emission:** the emitter checks again after instruction
   selection and register allocation. Any stack slot (including a spill), any
   frame operand, inline assembly, or an incoming stack parameter keeps the BP
   frame.

The second check is required: a function can be stack-free in SSA and still
spill under target register pressure.

## 8086 parameter caveat

The earlier plan showed ordinary parameters as `[sp+2]`, `[sp+4]`, and so on.
That is not a valid 8086 16-bit ModR/M addressing form: `SP` cannot be used as a
memory base there. The current routed BASIC/PASCAL ABI therefore still needs BP
for any procedure with stack parameters.

This is why the IR analysis does **not** reject parameters — that would bake an
8086 limitation into target-neutral SSA — while the x86-16 emitter does reject
the elision at the final ABI check. Parameterized frame elision can expand once
the routed back end has register parameters or another valid addressable-base
plan.

## Equivalent BASIC

Unchanged — this is an ABI/prologue decision after the middle end has removed
storage that has no observable identity.

## Remaining work

- Routed register-parameter ABI support so parameterized procedures can become
  frame-free too.
- Direct-emitter support; its O0021 register parameters are currently spilled
  into BP-relative homes on entry, so removing BP there needs a separate
  register-lifetime change rather than deleting four instructions.
- [O0170](O0170-leaf-register-save-elision.md) for any callee-stable registers a
  later allocator/emitter path chooses to use.
