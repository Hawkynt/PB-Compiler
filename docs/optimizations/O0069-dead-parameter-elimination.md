# O0069 — Dead parameters and call-shape cloning

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program analysis + emitter |
| **Related** | [O0018](O0018-interprocedural-constant-propagation.md), [O0021](O0021-register-parameters.md), [O0022](O0022-dead-procedure-elimination.md) |

## The idea

Two interprocedural transforms that pick up where
[O0018](O0018-interprocedural-constant-propagation.md) stops. IPCP specializes a
callee body but deliberately leaves the ABI alone; these change it:

1. **Dead-parameter elimination** — a parameter no reachable path in the callee
   reads is removed from the signature and from every call site, so its
   evaluation, its push and its frame slot disappear. (When IPCP has just
   replaced every read with a literal, the parameter is dead *by construction* —
   that is the common case.)
2. **Call-shape cloning** — a `SUB` called with one dominant argument shape gets
   a specialized clone for that shape while the general body remains for the
   other call sites.

## Applies to

```basic
SUB Draw(BYVAL mode%, BYVAL x%)
  IF mode% = 1 THEN PRINT "text"; x% ELSE PRINT "gfx"; x%
END SUB

CALL Draw(1, 10)
CALL Draw(1, 20)
```

## Today (after IPCP)

The body is specialized to the `mode% = 1` arm, but `mode%` is still evaluated,
pushed and given a frame slot at both call sites.

## Planned

```asm
    mov     ax, 000Ah
    push    ax
    call    Draw             ; one argument
    mov     ax, 0014h
    push    ax
    call    Draw
    ...
Draw:
    ...                      ; RET 2 instead of RET 4
```

## Equivalent BASIC

```basic
SUB Draw(BYVAL x%)
  PRINT "text"; x%
END SUB
CALL Draw(10)
CALL Draw(20)
```

## What it needs

- The same **ownership** proof [O0021](O0021-register-parameters.md) uses: every
  call site visible, no address taken, nothing linked that could call by name.
- Argument **evaluation order and side effects** must be preserved: a dropped
  argument whose expression has an effect (a function call, an `INCR`) must
  still be evaluated, just not passed.
- A cost model for cloning, so specialization does not double the image.
