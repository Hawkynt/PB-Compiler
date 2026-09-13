# Direct-emitter retirement

## Status: retired from production

The native DOS compiler no longer has two production code-generation routes.

The production path is now:

```text
source
  -> parse / bind
  -> IR lowering
  -> analysis-aware IR middle end
  -> x86-16 instruction selection
  -> machine register allocation / scheduling
  -> image/runtime/link emission
```

`pbc` always enables the IR/native backend and makes routing mandatory. A source body that cannot lower,
select or allocate is a compilation failure with the routing/selection reason; it is never silently handed
to the old direct body emitter.

`--no-x-backend` is therefore rejected. `--x-backend` and `--x-backend-strict` remain accepted only as
command-line compatibility spellings for the mandatory path and do not select a different compiler.

Normal `CodeGenerator` instances also start with IR routing enabled and mandatory. The historic
`UseExperimentalBackend` / `RequireBackend` properties remain temporarily source-compatible because a
number of differential tests explicitly instantiate the former direct path as a behavioral oracle. That
oracle is test infrastructure, not a production fallback.

## Why the cutover is safe

The repository already carries the decisive gate in `MandatoryRoutingTests`: the complete differential
corpus is compiled with routing mandatory in both optimizer modes. A routing decline is an error in that
fixture, so the gate cannot be satisfied by falling back to the direct emitter.

The direct implementation remains useful for a limited time as an independent behavioral oracle: tests can
compare the new lowering/selection path with the historical implementation while both exist in the source
tree. Keeping an oracle is different from keeping a production choice. New product behavior must not depend
on the oracle path.

## Remaining deletion work

Retirement and source deletion are intentionally separate. The following cleanup can happen after the
remaining differential fixtures have been converted to specification/oracle vectors:

1. replace tests that explicitly set `UseExperimentalBackend = false` with fixed expected behavior or
   external/vintage-compiler vectors;
2. remove `UseExperimentalBackend`, `RequireBackend`, `PBC_X_BACKEND` and `PBC_X_BACKEND_STRICT` compatibility
   state;
3. delete direct statement/expression body-emission code that is no longer referenced by tests;
4. retain the common DOS image, runtime, linker and assembler services used by the machine backend.

No production code may regain a decline-to-direct-emitter fallback during that cleanup.
