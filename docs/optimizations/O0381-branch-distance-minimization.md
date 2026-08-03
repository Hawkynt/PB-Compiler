# O0381 — Branch distance minimization

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0035](O0035-jump-relaxation.md), [O0382](O0382-post-layout-branch-relaxation.md), [O0365](O0365-maximum-weighted-fallthrough.md) |

## The idea

Minimize the **execution-weighted distance** between branches and their targets.
Short distances mean short encodings ([O0035](O0035-jump-relaxation.md)), fewer
bytes fetched, and on a paged target a branch that stays inside the current
page.

It is the secondary objective that rides along with fall-through maximization:
where two orders give the same fall-through weight, the one with shorter jumps
wins.

## What it needs

- Edge weights and a placement search
  ([O0365](O0365-maximum-weighted-fallthrough.md)) with distance as a second
  term in the cost function.
- Re-relaxation afterwards, since the whole point is that some near branches
  become short ones ([O0382](O0382-post-layout-branch-relaxation.md)).
- The metric itself — **weighted branch distance** — is one of the statistics
  the layout battery should report
  ([O0406](O0406-layout-assertion-battery.md)).
