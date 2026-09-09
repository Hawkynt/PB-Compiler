# O0382 — Post-layout branch relaxation

| | |
|---|---|
| **Status** | ✅ Implemented for O0360 terminal branches — post-link layout regenerates fall-through control and relaxes Jcc/JMP encodings to a monotone fixpoint |
| **Stage** | After layout |
| **Source** | `Emit/PostLinkLayout.cs` (`PostLinkLayoutRewriter`) |
| **Related** | [O0035](O0035-jump-relaxation.md), [O0360](O0360-basic-block-fragments.md), [O0093](O0093-jump-threading.md) |

## The idea

**Layout must not be the last step.** Moving blocks changes every displacement
and, more importantly, changes which CFG edge can be a fall-through.

For fragment-aware functions the linker therefore does not copy the old terminal
branch bytes. PBU2 records their semantics; after the new physical order is
known, `PostLinkLayoutRewriter` rebuilds the terminal control:

- an unconditional edge to the next fragment emits no jump;
- an unconditional non-fall-through edge emits `JMP`;
- a conditional with its false/secondary edge next emits one `Jcc` to the
  primary edge;
- a conditional whose primary edge is next is inverted so the other edge is the
  taken branch;
- when neither conditional successor is next, the linker emits `Jcc primary`
  followed by `JMP secondary`.

This is the same CFG with a new physical spelling; block layout never changes
program semantics.

## Relaxation

Generated control starts from a safe long form and only shrinks:

- `JMP rel16` (3 bytes) → `JMP rel8` (2 bytes) when the final signed-byte
  displacement fits;
- on 80386+ targets, `0F 8x rel16` (4 bytes) → `7x rel8` (2 bytes);
- on 8086/80186/80286 targets, the long conditional spelling is
  `J!cc +3 ; E9 rel16` (5 bytes), which likewise contracts to `7x rel8` when
  possible.

After any shrink, downstream block addresses change. The pass recomputes starts
and tries again until no encoding becomes shorter. Every transition strictly
reduces code size and no transition expands again, so convergence is monotone
and finite.

Retained non-terminal internal relative transfers are repatched against the
moved target. A retained short transfer that would become out of range is
rejected rather than guessed into a larger instruction, because expanding an
instruction inside an opaque fragment would require instruction-granular
fragment metadata that O0360 deliberately does not claim to have.

## Scope

This entry covers the post-layout branch work O0276 requires: fall-through
removal, conditional inversion, long/short selection and final relative fixups.
Other late layout ideas such as cold-block deduplication or profile-driven
alignment retain their own optimization IDs and are not silently bundled into
this pass.
