# O0268 — Profile collection and representation

| | |
|---|---|
| **Status** | 🟡 Partial — stable IR identities, block/edge/direct-call counts, merge and versioned persistence are implemented; target runtime collection and CLI plumbing remain |
| **Stage** | Compiler + runtime infrastructure |
| **IR** | ✅ `Ir/Profiling/IrProfile.cs`; blocks receive monotonic function-local `ProfileId`s when attached to an `IrFunction`, and `IrModule.Profile` is the shared consumer hook |
| **Verified by** | `IrProfileTests` — identity stability, object-based block/edge/call lookup, deterministic round-trip persistence, saturating merge, malformed-version rejection |
| **Related** | [O0269](O0269-profile-guided-inlining.md), [O0274](O0274-profile-guided-code-layout.md), [O0404](O0404-stale-profile-matching.md) |

## The idea

Every profile-guided optimization needs the same two things: a way to **produce**
execution counts, and a stable way to **attach** them to compiler objects.

Two production modes, as in every mature toolchain:

- **instrumented** — the compiler emits counters at block entries and call
  edges, the program writes them out at exit;
- **sampled** — a timer or hardware-counter interrupt records the instruction
  pointer, which is cheaper and unbiased but coarser. On DOS this means hooking
  `INT 08h` or a PIT-driven handler.

The representation is the harder half: counts must attach to **stable
identities** — procedure name plus a structural block ID — so a profile survives
compiler passes without depending on the block's current list position. Matching
profiles across changed source is a separate problem
([O0404](O0404-stale-profile-matching.md)).

## Implemented IR foundation

`IrFunction` assigns each block a monotonically increasing `ProfileId` the first
time it joins the function. Inserting a new entry or otherwise changing block
layout does not renumber surviving blocks. New blocks created by later transforms
receive new IDs instead of accidentally inheriting the heat of the block they
were cloned from.

`IrProfile` stores unsigned 64-bit counts for:

- block entries — `(function, block-id)`;
- CFG edges — `(function, source-block-id, target-block-id)`;
- direct calls — `(caller, block-id, call-ordinal, callee)`; the ordinal counts
  only calls, so unrelated arithmetic inserted earlier in the block does not
  perturb the call-site identity.

Profiles merge by saturating addition, so combining training runs cannot wrap a
hot counter back to cold. The JSON profile format carries an explicit magic name
and version, is written in deterministic key order, and rejects duplicate,
negative-ID, malformed and unsupported-version data on ingestion. A loaded
profile belongs on `IrModule.Profile`; consumers must treat a missing counter as
unknown and fall back to the same static heuristics they use when no profile is
attached.

The design follows the same separation used by LLVM instrumentation profiles:
serialized metadata maps counters back to compiler regions, while optimization
passes consume the resulting counts independently. GCC's `-fprofile-generate` /
`-fprofile-use` split is the model for the eventual producer/consumer CLI.

## Still planned

- `--profile-generate` / `--profile-use` CLI plumbing and target-runtime counter
  emission/flush for native images;
- sampled collection (DOS timer/PIT hook) and address-to-block mapping;
- stale-profile structural matching after source changes
  ([O0404](O0404-stale-profile-matching.md));
- downstream policies such as profile-guided inlining and layout — those consume
  this representation rather than being folded into the collection layer.

With no profile, or with no matching counter for a transformed block, the
optimizer must use the static heuristics of
[O0104](O0104-block-placement.md), so one pipeline remains correct in both modes.
