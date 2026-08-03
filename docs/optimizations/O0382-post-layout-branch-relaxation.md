# O0382 — Post-layout branch relaxation

| | |
|---|---|
| **Status** | ⬜ Planned (the relaxation itself exists — [O0035](O0035-jump-relaxation.md); running it *after* layout does not) |
| **Stage** | After layout |
| **Related** | [O0035](O0035-jump-relaxation.md), [O0360](O0360-basic-block-fragments.md), [O0093](O0093-jump-threading.md) |

## The idea

**Layout must not be the last step.** Moving blocks changes every displacement,
which creates new opportunities that the earlier passes could not see:

- a near branch whose new displacement fits the short form;
- a `JMP` that now targets the next instruction and disappears
  ([O0230](O0230-jump-to-next-removal.md));
- an inverted condition that now creates a fall-through
  ([O0094](O0094-branch-inversion.md));
- newly adjacent identical cold blocks that can be folded
  ([O0391](O0391-cold-code-deduplication.md)).

So the late pipeline is: **layout → relaxation → jump cleanup → alignment →
final fixups**, and each of those steps can feed the next.

## What it needs

- Iteration to a fixpoint, with care: relaxing a branch shortens the code, which
  shifts everything after it and may make another branch relaxable — the classic
  branch-relaxation convergence problem, which must be monotone to terminate.
- The assembler already owns every label and fixup, which is exactly what makes
  the re-run cheap.
