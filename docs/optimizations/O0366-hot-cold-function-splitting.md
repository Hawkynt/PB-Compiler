# O0366 — Hot/cold function splitting

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0105](O0105-hot-cold-splitting.md), [O0275](O0275-cold-code-outlining.md), [O0363](O0363-interprocedural-block-placement.md) |

## The idea

Split one procedure into **two independently placeable fragments** — a hot part
and a cold part — connected by a jump. The hot fragment joins the hot cluster;
the cold one goes to the cold region, where it costs no page and no cache line
until it runs.

Where [O0275](O0275-cold-code-outlining.md) creates a *callable* cold
procedure, this keeps one logical procedure whose halves simply live apart.

## What it needs

- Placeable fragments ([O0360](O0360-basic-block-fragments.md)) and block counts.
- The frame must remain valid across the split: the cold half runs with the same
  BP frame, so the jump is intra-procedural and the two halves cannot be
  separated by anything that changes the stack.
- Distance handling: a far-flung cold half may need a near rather than a short
  jump ([O0384](O0384-branch-island-minimization.md)).
