# Oracle toolchains

The differential test harness (`scripts/run-diff-tests.sh`) compiles every
`tests/diff/**/*.BAS` with both `pbc` **and** the genuine vintage compiler for
that dialect, then byte-compares the output. Those vintage compilers are
proprietary, so the raw binaries never live in the repository — only an
**AES-256-encrypted tarball per dialect** does:

| File | Dialect | Genuine oracle |
|------|---------|----------------|
| `pb35-toolchain.tar.enc` | `pb35` (default) | PowerBASIC 3.50 `PBC.EXE` |
| `pb30-toolchain.tar.enc` | `pb30` | PowerBASIC 3.0c `PBC.EXE` |
| `pb21-toolchain.tar.enc` | `pb21` | PowerBASIC 2.10 `PB.EXE` (IDE, autotype-driven) |
| `qb45-toolchain.tar.enc` | `qb45` | QuickBASIC 4.5 `BC`/`LINK`/`LIB` + `BCOM45.LIB` |
| `tb10-toolchain.tar.enc` | `tb10` | Turbo Basic 1.0 `TB.EXE` |
| `tb11-toolchain.tar.enc` | `tb11` | Turbo Basic 1.1 `TB.EXE` |
| `gw-toolchain.tar.enc` | `gw` | GW-BASIC interpreter `GWBASIC.EXE` |
| `basica-toolchain.tar.enc` | `basica` | BASICA interpreter `BASICA.COM` |
| `qbasic-toolchain.tar.enc` | `qbasic` | QBasic interpreter `QBASIC.EXE` (MS-DOS 5.0+) |

## How it works

`scripts/run-diff-tests.sh` decrypts each `tools/<dialect>-toolchain.tar.enc`
into `tools/<dialect>/` at run time **only when `PB_TOOLCHAIN_KEY` is set** (a
GitHub Actions repository secret in CI, an env var locally). A slot already
populated by a local install wins and is left untouched. Without the key the
harness skips the oracle batteries cleanly — the `pb35` vs `pb36` self-diff and
the NUnit suite remain the hard gates.

```bash
export PB_TOOLCHAIN_KEY=...        # never commit this
bash scripts/run-diff-tests.sh
```

## Interpreter oracles (GW-BASIC / BASICA / QBasic)

These dialects ship **no compiler** — the interpreter *is* the run. Their
batteries carry a `tests/diff/<dialect>/oracle.interpreter` template (instead of
`oracle.conf`): plain DOS commands that run the interpreter on `C:\T.BAS` with
the toolchain mounted as `D:`. The test program writes `RESULT.TXT` itself and
ends with `SYSTEM` so control returns to DOS. There is no `T.EXE` on the oracle
side; our side still compiles `T.BAS` with `pbc --dialect <dialect>` and runs the
EXE, then the two `RESULT.TXT` files are byte-compared. Stage the interpreter
binary into `tools/<dialect>/` (adjust the EXE name in `oracle.interpreter` to
match) and pack it like any other oracle.

## Adding / re-packing a dialect

```bash
# collect the compiler/interpreter + the runtime files it needs into
# tools/<dialect>/, then pack every populated slot (or just the named ones):
export PB_TOOLCHAIN_KEY=...                  # never commit this
bash scripts/pack-toolchains.sh              # all populated tools/<dialect>/
bash scripts/pack-toolchains.sh tb10 gw      # only these
```

Some install media (e.g. BASIC PDS 7.1) ship their files MS-compressed in the old
SZDD `"SZ "` variant (`.EX$`/`.LI$`/`.OB$`), which neither 7-Zip nor the modern
Windows `expand.exe` decode. `scripts/expand-szdd.ps1 <srcdir> <dstdir>` expands a
whole directory (mapping the trailing `$` back to `E`/`B`/`J`) so the files can be
staged before packing.

`pack-toolchains.sh` is the exact inverse of the harness's decrypt step, so a
container always round-trips. The raw `tools/<dialect>/` directories are
git-ignored; only the `.enc` tarballs are tracked. Each tarball's top level holds
the files the harness mounts (e.g. `PBC.EXE` for PB dialects; the `BC`/`LINK`
chain for QB via `oracle.conf`; the interpreter EXE via `oracle.interpreter`).
