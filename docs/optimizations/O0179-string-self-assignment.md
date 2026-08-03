# O0179 — String self-assignment elimination

| | |
|---|---|
| **Status** | ⬜ Planned (the non-string self-copy is already elided — [O0015](O0015-udt-zero-cost.md)) |
| **Stage** | Emitter |
| **Related** | [O0015](O0015-udt-zero-cost.md), [O0009](O0009-string-temp-economy.md), [O0178](O0178-empty-string-simplification.md) |

## The idea

`s$ = s$` is a no-op — but only if it is elided *correctly*. The naive lowering
duplicates the string, frees the old handle and stores the new one, which
allocates and copies for nothing; a careless elision, on the other hand, could
leave a handle double-freed or a descriptor stale.

[O0015](O0015-udt-zero-cost.md) already elides the structurally identical
non-string self-copy (`rec = rec`) and folds the self-compare. Strings were
deliberately excluded there because of exactly this ownership question.

## Applies to

```basic
DIM s$
s$ = s$
IF s$ = s$ THEN PRINT "equal"       ' always true, no comparison needed
```

The pattern is rarer in hand-written code than `rec = rec`, but it appears
routinely after inlining and specialization, where a copy's source and target
become the same variable.

## Today

```asm
    call    StrDup           ; allocate and copy
    call    StrAssign        ; free the old handle, store the new one
```

— a full round trip through the heap that ends where it started, plus a possible
compaction.

## Planned

Nothing is emitted, and the self-comparison folds to −1 exactly as the UDT
self-compare does.

## What it needs

- The same structural lvalue-identity test (`SameLValue`)
  [O0015](O0015-udt-zero-cost.md) uses, extended to dynamic strings.
- A **reference-count/ownership review**: eliding must leave the handle, the
  descriptor and the heap in precisely the state the real sequence would have —
  which for a self-assignment means untouched.
