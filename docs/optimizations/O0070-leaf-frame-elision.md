# O0070 — Leaf-frame elision

| | |
|---|---|
| **Status** | 🟡 Partial — IR-routed frame-free procedures, including register-resident stack parameters |
| **Stage** | IR middle-end analysis + routed x86 emitter |
| **Related** | [O0019](O0019-zero-elision.md), [O0021](O0021-register-parameters.md), [O0006](O0006-inlining.md), [O0170](O0170-leaf-register-save-elision.md) |

## The idea

[O0019](O0019-zero-elision.md) removes unnecessary frame *zeroing*. O0070 removes
the **persistent frame itself** when the function has no state that needs one.
After SROA and `mem2reg`, a procedure whose fixed local state has disappeared can
often keep every value in SSA/register form; if register allocation also needs
no spill slots, the routed emitter can omit the persistent `PUSH BP` / `MOV BP,SP`
prologue and the `MOV SP,BP` / `POP BP` epilogue.

A call does not by itself require a private frame. A non-leaf procedure remains
eligible when its own state is frame-free and the target calling sequence does
not need BP for anything persistent.

## Applies today

```basic
$OPTIMIZE SPEED
FUNCTION AddOne%(BYVAL value%) NOINLINE
  LOCAL result%
  result% = value% + 1
  AddOne% = result%
END FUNCTION
```

After scalar promotion the local has no storage identity, so the IR contains no
`alloca`. If selection and register allocation keep `value%` in a register and
introduce no spill slot, the routed x86-16 emitter needs BP only long enough to
read the incoming stack argument:

```asm
AddOne:
    push    bp
    mov     bp, sp
    mov     ax, [bp+4]
    pop     bp
    inc     ax
    ret     2
```

instead of keeping BP live throughout the body and restoring SP from it at every
return:

```asm
AddOne:
    push    bp
    mov     bp, sp
    mov     ax, [bp+4]
    inc     ax
    mov     sp, bp
    pop     bp
    ret     2
```

A parameterless frame-free procedure still has no BP traffic at all.

## Two-stage proof

The implementation deliberately splits the decision:

1. **IR middle end:** `FrameElision.IsCandidate` runs after the normal optimizer
   has had the chance to remove scalar/aggregate stack storage. A surviving
   `alloca`, error-handler state, or inline assembly makes the function
   ineligible. Calls and parameters do not.
2. **Final machine emission:** the emitter checks again after instruction
   selection and register allocation. Any stack slot (including a spill), any
   body operand that is still a `StackSlot`/`ParamCell`, or inline assembly keeps
   the persistent BP frame. Register-resident incoming parameters may instead
   use a short BP staging window at entry.

The second check is required: a function can be stack-free in SSA and still
spill under target register pressure. In particular, the spiller can replace an
incoming argument register with a `ParamCell`; that transformation deliberately
turns O0070 back off for that function because the body now reads `[BP+disp]`.

## 8086 parameter caveat

The earlier plan showed ordinary parameters as `[sp+2]`, `[sp+4]`, and so on.
That is not a valid 8086 16-bit ModR/M addressing form: `SP` cannot be used as a
memory base there. The routed BASIC/PASCAL ABI therefore establishes BP while it
copies used register-resident arguments from their caller-owned stack cells.
Once those copies are complete, BP is immediately restored and the frame-free
body runs without it.

This is why the IR analysis does **not** reject parameters — that would bake an
8086 limitation into target-neutral SSA. The target-specific final check instead
distinguishes two cases that the IR correctly treats alike: an argument that has
become an ordinary machine register needs BP only at entry, while an argument
that survived/spilled as `ParamCell` needs BP for the whole function.

## Equivalent BASIC

Unchanged — this is an ABI/prologue decision after the middle end has removed
storage that has no observable identity.

## Remaining work

- Routed register-parameter ABI support can remove even the transient BP staging
  window for parameters already delivered in registers.
- Direct-emitter support; its O0021 register parameters are currently spilled
  into BP-relative homes on entry, so removing BP there needs a separate
  register-lifetime change rather than deleting four instructions.
- [O0170](O0170-leaf-register-save-elision.md) for any callee-stable registers a
  later allocator/emitter path chooses to use.
