# O0293 — Copy-on-write elision

| | |
|---|---|
| **Status** | 🟨 Partial — straight-line SSA string ownership sharing implemented; path-sensitive/runtime COW remains planned |
| **Stage** | IR middle end |
| **Gate** | Ordinary optimizer |
| **Source** | `Ir/Passes/StringCopyOnWriteElision.cs`, registered as `strcow` after `mem2reg2` |
| **Verified by** | `PowerBasic.Compiler.Tests/Ir/StringCopyOnWriteElisionTests.cs` |
| **Related** | [O0291](O0291-handle-ownership-elision.md), [O0296](O0296-string-move-instead-of-copy.md), [O0297](O0297-substring-view.md), [O0208](O0208-inplace-literal-append.md) |

## The idea

A copy made only because two names might both be live is unnecessary while the
underlying string value remains immutable. The useful observation in the current
IR is that `mem2reg` already turns local string cells into SSA ownership
lifetimes:

```text
%a = ... owned string handle ...
%b = call ptr @rt_str_dup(ptr %a)   ; b$ = a$
...
call void @rt_str_free(ptr %b)      ; one name dies
...
call void @rt_str_free(ptr %a)      ; the other name dies
```

When every raw use of `%a` and `%b` is either another `rt_str_dup` borrow or one
`rt_str_free`, and all those uses are in the same basic block, `%b` can simply be
replaced by `%a`. The earlier of the two frees is removed and the later free owns
the one shared handle:

```text
%a = ... owned string handle ...
...                              ; both BASIC names refer to %a
...
call void @rt_str_free(ptr %a)   ; last name dies
```

This is delayed duplication without changing the runtime representation. A
subsequent assignment or in-place-style string operation already obtains an owned
copy with `rt_str_dup` before lowering frees the old variable value. After O0293,
that existing borrow becomes the detach point: the expensive byte copy happens at
the first mutation, not at `b$ = a$`.

For example:

```basic
DIM a$, b$
a$ = "one"
b$ = a$              ' eager copy removed
b$ = b$ + "x"        ' existing owned read duplicates here instead
PRINT a$              ' still "one"
PRINT b$              ' still "onex"
```

## Safety proof used by the implemented slice

`StringCopyOnWriteElision` deliberately accepts only a shape for which ownership
can be proven from exact SSA use lists:

- the candidate is an `rt_str_dup(source)`;
- both the source value and duplicate have exactly one `rt_str_free` use;
- every other raw use of either value is another `rt_str_dup` borrow;
- all uses and both releases are in the candidate's basic block;
- every borrow precedes the corresponding value's release;
- functions with error handlers or inline assembly are skipped like the rest of
  the ownership-sensitive middle end.

The transform rewrites the duplicate's users to the source, erases the eager
`rt_str_dup`, and erases whichever release occurs first. Keeping the later release
is what makes the shared handle remain valid across the union of both original
lifetimes.

Unknown/consuming calls, PHIs, address-like escapes and cross-block lifetimes are
not guessed about. They simply decline. This follows the same conservative
principle used by standard alias/ModRef analyses: an optimization needs a proof
that an access cannot mutate or escape the object, not merely an absence of an
obvious write.

## Interaction with in-place append

The pass runs as a function pass after the second `mem2reg`, before the late string
module passes. That ordering is intentional. O0293 can first turn:

```text
%b = rt_str_dup(%a)
%tmp = rt_str_dup(%b)
%new = rt_str_concat(%tmp, ...)
rt_str_free(%b)
```

into the delayed form using `%a`. Later `StringAppendInPlace` may consume `%a`
directly only if its existing uniqueness/death proof says the source itself has
no other readers. If `a$` remains live, that pass declines and the mutation keeps
the duplicate which O0293 deliberately delayed to that point.

## Still planned

The current slice does **not** introduce reference counts, shared descriptor bits,
or a runtime detach operation. Those are still required to cover cases where the
ownership relation crosses CFG edges or escapes into operations whose mutation
behaviour cannot be proven locally. A future extension can choose between:

1. path-sensitive SSA lifetime reasoning that proves which alias owns the last
   release on every path; or
2. true runtime shared ownership plus uniqueness testing/detach-on-write.

The second option is substantially larger because the DOS descriptor table and
all mutating runtime entries must agree on the sharing protocol.

## References and licensing

- Swift's official `isKnownUniquelyReferenced(_:)` documentation describes the
  standard copy-on-write rule: mutate storage in place only when it is uniquely
  referenced; otherwise copy before mutation.
- LLVM's official Alias Analysis documentation provides the conservative
  Must/May/NoAlias and Mod/Ref model used here as the reference for declining
  uncertain mutation/escape cases.

No implementation code was copied or translated from those sources. The pass and
tests are an original implementation against PB-Compiler's existing ownership IR;
external material was used only as behavioural/design reference.
