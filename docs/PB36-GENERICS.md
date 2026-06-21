# PB 3.6 compile-time generics (monomorphization) — design plan

**Status: planned (not yet implemented).** A design for generic `TYPE`s and
procedures that are *fully vivified into concrete implementations at compile time*
— like C++ templates / Rust / .NET value-type generics: no runtime type info, no
boxing, no dispatch. Each distinct instantiation becomes an ordinary concrete TYPE
or procedure, after which the existing object-model machinery
([PB36-TYPES.md](PB36-TYPES.md)) handles it unchanged.

This rides directly on the existing model: a generic `Stack OF T` is just a
template; `Stack OF LONG` substitutes `T := LONG` to produce a concrete `Stack`
UDT whose members lift exactly as today.

## Surface syntax

Recommended: the VB-idiomatic **`OF`** keyword (parens conflict with array bounds,
angle brackets conflict with the `<` / `>` operators).

```basic
TYPE Stack OF T
  Count AS INTEGER
  Items(1 TO 100) AS T
  SUB Push(BYVAL v AS T)
    INCR THIS.Count
    THIS.Items(THIS.Count) = v
  END SUB
  FUNCTION Pop() AS T
    Pop = THIS.Items(THIS.Count)
    DECR THIS.Count
  END FUNCTION
END TYPE

DIM s AS Stack OF LONG          ' instantiates the concrete Stack OF LONG
s.Push(10) : PRINT s.Pop()
```

- **Multiple parameters**: `TYPE Pair OF (K, V)` / `DIM p AS Pair OF (INTEGER, STRING)`
  (parens required for >1 to keep `DIM a AS T OF X, b AS U` unambiguous; a single
  parameter needs no parens).
- **Generic procedures**: `FUNCTION Max OF T (BYVAL a AS T, BYVAL b AS T) AS T`.
  Call as `Max(3, 4)` (T inferred from arguments) or `Max OF LONG (3, 4)` (explicit).
- **Generic generators / constructors / properties / READONLY** all follow for free —
  the instantiated TYPE is a normal TYPE, so generators, `THIS`-constructors, auto
  properties and `READONLY` work per-instantiation with no extra machinery.

## Mechanism: a pre-bind monomorphization pass

A new pass runs **before** the binder's main scan, turning templates + use-sites
into concrete declarations the rest of the compiler already understands.

1. **Collect templates.** Parse a `TYPE`/`SUB`/`FUNCTION` carrying an `OF <params>`
   list into a *template* (its AST + the type-parameter names). Templates are NOT
   bound or lowered directly — they have no concrete layout.
2. **Discover instantiations.** Scan the whole program for instantiation sites:
   `AS <Generic> OF <args>` in DIM / parameters / fields / `NEW`, and generic
   procedure calls (explicit `OF` or inferred from argument types). Each yields a
   `(template, type-args)` key.
3. **Fixpoint.** Substituting a template can reference *other* generics
   (`Stack OF T` whose field is `List OF T` ⇒ needs `List OF LONG`). Iterate a
   work-list until no new instantiation appears. Dedupe by key so each concrete
   form is produced once.
4. **Substitute.** For each instantiation, clone the template AST with a
   `TypeName` rewriter mapping each type parameter (`TypeName(None, UserTypeName:
   "T")`) to its concrete `TypeName`, across fields, member signatures, and member
   bodies (including `DIM`s and casts inside bodies). Emit a concrete `TypeDecl` /
   procedure named by a **collision-free mangle** — e.g. `Stack@LONG`,
   `Pair@INTEGER@STRING` (the `@`/`.` separators can't occur in a user identifier,
   like the existing `Type.Member` mangling).
5. **Bind normally.** Inject the concrete declarations into the unit; the existing
   `DefineUdt` / `DefineTypeMembers` / member-call resolution handle them with zero
   changes. `DIM s AS Stack OF LONG` resolves to the concrete `Stack@LONG` UDT.

Result: after this pass the program contains only concrete types — the generic
surface has been fully "vivified", and the back end is unchanged.

## Type inference for generic procedures

`Max(3, 4)` binds the arguments, unifies each parameter's declared type pattern
against the argument type (`a AS T`, `b AS T` ⇒ `T = INTEGER`), checks consistency,
then resolves the call to the `Max@INTEGER` instantiation. Explicit `Max OF LONG`
skips inference. Mirrors the existing overload-resolution tie-break that already
ranks by argument-type match.

## Open decisions

- **Constraints** (later): `OF T AS NUMERIC` / `AS ANY` for friendly diagnostics
  and to gate operator use. v1 is unconstrained — an unusable `T` surfaces as an
  ordinary error inside the instantiated body (C++ pre-concepts style).
- **Recursive generics**: distinct `(template,args)` keys terminate the fixpoint,
  but a *direct, non-pointer* self-containing field (`Tree OF T` with a `Tree OF T`
  field) is infinite-size and must error like any recursive TYPE; a `Tree OF T PTR`
  field is fine.
- **Separator char** for the mangle (`@` vs `.`) — pick one unused elsewhere.
- **Inference depth** — v1 infers only from direct parameter types (no nested
  `Stack OF T` argument unification); widen later if needed.

## Verification

pb36-only surface with **no differential oracle** (PBC 3.50 has no generics):
verify each piece by *execution* (compile + run in DOSBox vs. a hand-computed
expected) plus binder/parser unit tests, and keep the full differential harness
byte-identical (generics are inert for every existing battery — no template, no new
code). Suggested first slice: a single-parameter generic `TYPE … OF T` with one
method, instantiated at two concrete types, proving both monomorphize and run; then
generic procedures with inference; then transitive/fixpoint instantiation.

## Why it fits now

The object model already lowers every member to a concrete procedure keyed by the
receiver's static type, and already mangles names with characters users can't type.
Generics add exactly one front-end stage — substitute-and-name — ahead of that, so
the "cool stuff resolved entirely at compile time" is a source-to-source expansion
with no runtime surface and no back-end change.
