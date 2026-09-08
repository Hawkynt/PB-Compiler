# O0291 — Handle ownership elision

| | |
|---|---|
| **Status** | ✅ Implemented (same-block SSA ownership lifetimes) |
| **Stage** | Mid-end |
| **IR** | `PowerBasic.Compiler/Ir/Passes/HandleOwnershipElision.cs` |
| **Related** | [O0179](O0179-string-self-assignment.md), [O0296](O0296-string-move-instead-of-copy.md), [O0260](O0260-escape-analysis.md) |

## The idea

The string manager's discipline is: assigning a value **duplicates** it and
frees the old handle; leaving scope **frees** it. Matching dup/free pairs that
provably bracket no ownership-changing use cancel out and can both be removed —
the BASIC equivalent of eliding a balanced retain/release pair.

## Applies to

```basic
DIM a$, b$
a$ = b$                      ' dup b$, free a$'s old handle, store
PRINT a$
' a$ dies here: free
```

After local string storage has been promoted to SSA, the ownership part of that
shape is explicit:

```text
%a = call rt_str_dup(%b)
%print-copy = call rt_str_dup(%a)
call rt_print_strvar(%print-copy)
call rt_str_free(%a)
call rt_str_free(%b)
```

O0291 proves that `%b` outlives `%a`, rewires the read-copy to `%b`, and removes
`rt_str_dup(%b)` plus `rt_str_free(%a)`. The print still receives its own owned
temporary, so the consuming runtime ABI is unchanged:

```text
%print-copy = call rt_str_dup(%b)
call rt_print_strvar(%print-copy)
call rt_str_free(%b)
```

If the copied owner is never read at all, a same-block `rt_str_dup` / matching
`rt_str_free` pair is removed directly. If `b$` itself is dead after the
assignment, transferring ownership instead is the broader O0296 case.

## IR implementation

`HandleOwnershipElision` runs immediately after the second `Mem2Reg` sweep. At
that point ordinary local string cells have become SSA handles and the intrusive
use-lists describe every direct owner operation exactly.

The implemented proof is conservative by construction:

- the copied owner and its one matching `rt_str_free` must be in the same basic
  block;
- every other use of that owner must be `rt_str_dup(owner)`, because those are
  ownership-preserving reads that still create a fresh handle for consuming
  operations;
- when such reads exist, the source must be an SSA handle (`argument`, call,
  `phi` or `select`, or the null handle), not a load from aliasable storage;
- every direct use of the non-null source must be visible in the same block and
  must itself be a duplicate or its one release; and
- the source release must occur after the copied owner's release.

A direct/opaque use, a source that dies first, reloaded/escaped storage, a
cross-block lifetime, armed error handling or inline assembly makes the pass do
nothing. There is deliberately no attempt to infer that an arbitrary call
borrows a handle: one mistaken ownership assumption is a double free, not a
minor missed optimization.

## References

The legality rule follows the same balanced-pair principle used by ownership
optimizers elsewhere: Clang ARC permits retain/release removal only as paired
lifetime transformations, and LLVM's ARC optimizer models elimination over
proved retain/release paths. Those sources were used as semantic references;
the PB-Compiler implementation is original and is expressed entirely in this
repository's explicit `rt_str_dup` / `rt_str_free` protocol.

- [Clang — Automatic Reference Counting](https://clang.llvm.org/docs/AutomaticReferenceCounting.html)
- [LLVM — ARC Optimization](https://llvm.org/docs/doxygen/group__ARCOpt.html)
