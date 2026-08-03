# O0200 — Trivial TYPE method and property inlining

| | |
|---|---|
| **Status** | ✅ Implemented (`pb36` object model) |
| **Stage** | Emitter |
| **Source** | `CodeGen/OptInlining.cs` |
| **Gate** | `--optimize` |
| **Split from** | [O0006](O0006-inlining.md) |

## What it is

Any **trivial** method body — an auto-generated property accessor or a
hand-written one-expression method — is inlined at its call sites, with the
`THIS` receiver treated as the ordinary BYREF argument it is.

There is no property-specific path: `o.Count` on an anonymous property and a
hand-written `FUNCTION Sum() = THIS.x + THIS.y` inline through exactly the same
machinery, so the object model costs nothing at run time.

## Sample

```basic
TYPE Counter
  PROPERTY Count AS LONG          ' anonymous: getter + setter over a hidden field
END TYPE

DIM c AS Counter, n&
c.Count = 5
n& = c.Count + 1
```

## With the optimizer

```asm
    mov     word ptr [c], 0005h   ; the setter body, inlined
    mov     ax, [c]
    mov     dx, [c+2]
    add     ax, 0001h             ; the getter body, inlined
    adc     dx, 0000h
```

— identical to what a bare field access would emit, and both accessors are
purged from the image once every call site is inlined
([O0201](O0201-inlined-procedure-purge.md)).

## Why it is safe

The inline gate is the leaf gate of [O0006](O0006-inlining.md): BASIC
convention, not `STATIC`, no `ON ERROR`, no capture, and a body of a few plain
scalar assignments. `THIS` is passed BYREF like any other reference parameter, so
mutation through it behaves identically.
