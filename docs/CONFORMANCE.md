# Dialect conformance testing

Dialect conformance is an accept/reject and execution contract, not a collection of programs that
happen to compile. A feature is covered only when the suite contains its valid spellings, optional-
parameter combinations, invalid structures, and cross-dialect negative cases.

## Test lanes

| Lane | Material | Contract |
|---|---|---|
| Statement surface | `StatementSurface` and `StatementSurfaceCensusTests` | Every isolated statement/parameter form is accepted exactly in the dialects that provide it. Rejection must be a lexer, parser, preprocessor, or binder diagnostic—not an internal exception. |
| Structurally invalid source | `InvalidSyntaxSurfaceTests` | Syntax belonging to no dialect is rejected cleanly under every advertised dialect. |
| Compiler execution | ordinary codegen/IR/DOSBox tests | Both the direct and routed x86-16 paths produce an executable with the expected behavior. |
| Genuine syntax oracle | `StatementSurfaceOracleMaterialTests` and `scripts/run-syntax-oracle-tests.sh` | Vintage DOS compilers independently accept or reject each generated source file. Disagreements are written to `build/conformance/syntax/oracle-results.tsv`. |
| Genuine runtime oracle | `tests/diff/<dialect>` and `scripts/run-diff-tests.sh` | The generated executable and original compiler/interpreter produce identical observable output. |

The syntax-oracle runner deliberately skips BASICA, GW-BASIC, and QBasic. They have no compile step,
and interpreted BASIC can defer syntax checking until a statement executes; treating “the
interpreter loaded the file” as compiler acceptance would be a false result. Interpreter syntax
quirks therefore use focused executable differential programs, such as `DEADTEXT.BAS`, alongside
front-end diagnostics.

Useful syntax-oracle filters:

```bash
DIALECTS=pb30,pb35 FORMS=select.case,redim.preserve \
  scripts/run-syntax-oracle-tests.sh

# Zero means the complete selected matrix; use a small number while developing the harness.
MAX_CASES=25 DIALECTS=qb45 scripts/run-syntax-oracle-tests.sh
```

The encrypted vintage toolchains are unlocked with `PB_TOOLCHAIN_KEY`, exactly as for the runtime
differential harness. Missing toolchains are recorded as skips rather than guessed results.


## Per-dialect batteries

`tests/dialects/` holds one battery per dialect plus a generated index. Each battery states twelve
claims and marks each one held, partial, unprobed or not applicable - and the page is WRITTEN BY
`DialectBatteryTests` from what its probes measured, so it cannot claim a dimension holds after the
code stopped holding it. An empty box means nobody has checked, which is deliberately distinct from a
failing one: in a green test run those two look identical and must not read identically.

The twelve are: statement syntax and parameter combinations, lowering to the IR, syntax errors in
unreachable branches (ignored and warned), syntax errors on reachable flow (rejected), foreign-dialect
syntax (rejected), numeric typing, runtime-implementation selection, runtime behaviour, metastatements
and their effect on the image, quirk reproduction, bit-exact arithmetic, and the README itself.

## Line-numbered interpreters

BASICA and GW-BASIC source has a numeric line number on every non-empty physical program line,
including comment-only lines. The number is a real label and must be in the interpreter's accepted
range. These dialects reject named labels and compiled-language block syntax such as multi-line
`IF`, `DO/LOOP`, `SELECT CASE`, and `SUB`/`FUNCTION`; QuickBASIC and later dialects accept their own
documented forms without mandatory numbering.

Generated conformance probes are rendered per dialect only for physical requirements such as line
numbers. They do not translate a named label into a numeric one, because the oracle's rejection of
that exact spelling is part of the result.

## Deferred interpreter source

BASICA/GW-BASIC may store statement text that is not syntax-checked until execution reaches it. The
front end represents unparsed inline text as `DeferredSourceStmt` and issues a warning. This node has
no invented runtime meaning:

- if constant control flow proves the containing branch unreachable, code generation discards it;
- if the statement is reachable, or reachability cannot be proved, code generation fails with a
  diagnostic;
- the rule applies even when optimization is disabled, because it is a correctness check rather
  than an optimization.

The compiler is intentionally conservative. Unsupported unstructured control flow does not count as
proof that deferred text is dead.

## Adding coverage

For each syntax or runtime quirk:

1. Add every valid parameter/omission/order form to the statement surface or a focused fixture.
2. Set both family/version minima; `null` means the family never provided that spelling.
3. Add a negative case for the dialect immediately before introduction and for the other family when
   applicable.
4. Add malformed forms that must reject in all dialects.
5. Add a DOSBox execution case when behavior, formatting, storage, errors, or side effects differ.
6. Add or extend a genuine-oracle case before declaring an uncertain historical rule verified.

Dialect-specific runtime behavior should remain explicit (`rt_*` entry points or dialect-selected
runtime branches) so a shared parser never erases observable differences.
