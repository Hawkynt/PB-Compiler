# Dialect conformance testing

Dialect conformance is an accept/reject and execution contract, not a collection of programs that
happen to compile. A feature is covered only when the suite contains its valid spellings, optional-
parameter combinations, invalid structures, and cross-dialect negative cases.

## Test lanes

| Lane | Material | Contract |
|---|---|---|
| Statement surface | `StatementSurface` and `StatementSurfaceCensusTests` | Every isolated statement/parameter form is accepted exactly in the dialects that provide it. Rejection must be a lexer, parser, preprocessor, or binder diagnostic—not an internal exception. |
| Statement backend surface | `StatementSurfaceBackendTests` | Every accepted catalog form reaches verified Low IR and explicit COM/MZ emission across every declared dialect and both optimizer modes, without unexpected routing declines or emitted bodies lacking IR definitions. |
| Structurally invalid source | `InvalidSyntaxSurfaceTests` | Syntax belonging to no dialect is rejected cleanly under every advertised dialect. |
| Compiler execution | ordinary codegen/IR/DOSBox tests | The mandatory multi-stage IR/x86-16 path produces an executable with the expected behavior, without direct-emitter fallback. |
| Genuine syntax oracle | `StatementSurfaceOracleMaterialTests` and `scripts/run-syntax-oracle-tests.sh` | Vintage DOS compilers independently accept or reject each generated source file. Disagreements are written to `build/conformance/syntax/oracle-results.tsv`. |
| Genuine runtime oracle | `tests/diff/<dialect>` and `scripts/run-diff-tests.sh` | The generated executable and original compiler/interpreter produce identical observable output. |

The backend-surface gate renders each source using its dialect's physical-line rules, including
numeric labels for BASICA/GW-BASIC. It checks the requested artifact signature, the actual retained
Low IR module and its verifier, and the IR definition behind every tracked emitted body. Bodiless
external link declarations are not fallback bodies. A retained main body must actually be emitted;
an empty emitted-body list cannot satisfy that check. The catalog must list every declared dialect.
This is compilation coverage of the catalog, not execution coverage of every feature combination,
intrinsic overload, calling convention or runtime boundary; those still need focused oracle tests.
The 76 corpus-wide backend configurations carry `Slow` metadata: the fast smoke tier excludes them,
while both required core jobs include them. The cheap catalog-completeness test remains in smoke and
guards that classification. No core filter, assertion or workflow timeout is relaxed.

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

## Runtime-oracle precision

CI runs the differential battery with headless DOSBox-X in an independent job. The protected
`ubuntu-latest` status requires both that job and the complete Windows/Linux core matrix to succeed;
failure, cancellation, skipping or a missing result cannot satisfy it. Both execution jobs retain
their existing 90-minute limits. The ordinary core tests retain classic DOSBox for speed.
To select the same oracle emulator
locally, run `DOSBOX_EXE="$(command -v dosbox-x)" bash scripts/run-diff-tests.sh`.

Keeping the lanes parallel avoids sharing one budget between dependency installation, the full
compiler matrix and the vintage batteries. The previous combined job reached its 90-minute limit
after a 23-minute installation and 32-minute core run, interrupting otherwise passing comparisons.
`bash scripts/tests/ci-result-gate-test.sh` checks every pair of job outcomes and malformed inputs;
the aggregate runs even after a dependency fails so it reports failure rather than a skipped check.

Emulator precision is part of the measurement. DOSBox 0.74's double-based FPU loses bits from
MBF64 cells and large QUAD values, and changes some QB transcendental results. On 2026-10-26,
fresh GW-BASIC 3.23 captures of `tests/diff/gw/MBFABS.BAS` agreed between classic DOSBox and Staging;
all eight compiled direct/round-trip, plain/optimized COM/EXE images matched the capture under
Staging but lost DOUBLE bits under classic DOSBox. Linux DOSBox-X preserved those bits with the
runtime harness's `core=auto` profile. Output comparisons are not relaxed to hide this difference.

Missing-output failures preserve the source, available text outputs, emulator log, configuration and
guest-file names/sizes under a fresh `build/diff-failures/run.*/<dialect>/<case>/<lane>/` directory.
The next case cannot overwrite this evidence. CI uploads these captures beside its test results;
oracle executables, libraries, objects and arbitrary guest files are excluded. Logging stays outside
the mounted guest directory so it cannot change file-enumeration results. Run
`bash scripts/tests/diff-diagnostics-test.sh` to check failure retention and capture isolation.

The 2026-10-27 CI run passed PB35/PB36's direct and round-trip COMMON handoff checks, all core tests
and all completed numeric comparisons, but the genuine TB11 IDE timed out on `DIFF02.BAS`.
Earlier runs timed out on `DIFF03.BAS` or completed both. This intermittent oracle failure remains
unexplained; diagnostic capture is not a claimed fix, retry policy or relaxed comparison.

## DOS execution-state coverage

`ExecAllocationStateTests` executes COMMON self-handoff with plain/optimized COM and MZ images,
including the automatic optimized container. It checks the complete `DIFF21.BAS` output and handoff
consumption. Native child-exit tests require status 37 exactly and preserve the parent's allocation
policy, including explicit first-fit/best-fit DOS changes made by inline assembly.

On 2026-10-27, fresh DOSBox-X runs isolated an optimized COM failure to the runtime's startup
high-first preference: changing only that operand restored both CHAIN passes. The runtime now
suspends that private preference around EXEC, snapshots/restores the actual caller state around
SHELL, and preserves AX while restoring DOS state on exit. Five fresh direct/automatic COM/MZ
profiles and ten PB35/PB36 round-trip profiles then produced identical complete output;
unoptimized image bytes were unchanged.
This does not change container selection or complete failed-EXEC error mapping and constrained-memory
placement coverage, which remain separate runtime work.

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
