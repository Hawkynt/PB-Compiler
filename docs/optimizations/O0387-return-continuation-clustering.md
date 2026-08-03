# O0387 — Return-continuation clustering

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0386](O0386-caller-callee-colocation.md), [O0364](O0364-hot-path-block-chaining.md) |

## The idea

A call's **continuation** — the block that runs when the callee returns — is
fetched immediately after the callee's last instruction. Placing the common
continuation near the callee improves temporal fetch locality: the return lands
in code that is already in the queue, the line or the page.

It is the return-edge counterpart of
[O0386](O0386-caller-callee-colocation.md)'s call edge, and it matters most when
a callee is small and hot.

## What it needs

- Return-edge weights, which for a direct call are the call's own counts, but
  for a shared callee split across many callers require per-call-site attribution
  ([O0268](O0268-profile-collection.md)).
- The usual conflict: one callee has many continuations, and only one can be
  adjacent — so the placement is a weighted choice, not a rule.
