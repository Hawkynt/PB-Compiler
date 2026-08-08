# Oracle toolchains

The differential test harness (`scripts/run-diff-tests.sh`) compiles every
`tests/diff/**/*.BAS` with both `pbc` **and** the genuine vintage compiler for
that dialect, then byte-compares the output. Those vintage compilers are
proprietary, so the raw binaries never live in the repository — only an
**AES-256-encrypted tarball per dialect** does:

| File                       | Dialect          | Genuine oracle                                  |
| -------------------------- | ---------------- | ----------------------------------------------- |
| `pb35-toolchain.tar.enc`   | `pb35` (default) | PowerBASIC 3.50 `PBC.EXE`                       |
| `pb30-toolchain.tar.enc`   | `pb30`           | PowerBASIC 3.0c `PBC.EXE`                       |
| `pb21-toolchain.tar.enc`   | `pb21`           | PowerBASIC 2.10 `PB.EXE` (IDE, autotype-driven - needs dosbox-staging or DOSBox-X) |
| `qb45-toolchain.tar.enc`   | `qb45`           | QuickBASIC 4.5 `BC`/`LINK`/`LIB` + `BCOM45.LIB` |
| `tb10-toolchain.tar.enc`   | `tb10`           | Turbo Basic 1.0 `TB.EXE`                        |
| `tb11-toolchain.tar.enc`   | `tb11`           | Turbo Basic 1.1 `TB.EXE`                        |
| `gw-toolchain.tar.enc`     | `gw`             | GW-BASIC interpreter `GWBASIC.EXE`              |
| `basica-toolchain.tar.enc` | `basica`         | BASICA interpreter `BASICA.COM`                 |
| `qbasic-toolchain.tar.enc` | `qbasic`         | QBasic interpreter `QBASIC.EXE` (MS-DOS 5.0+)   |

### C compilers (OMF object interop)

These are not BASIC dialects — they back `CInteropTests` (see `docs/LINKER.md`),
which proves our OMF object reader + linker integrate genuine foreign C objects.
Each emits a slightly different OMF flavour, and all four must link cleanly:

| File                      | Slot    | Genuine compiler                                       |
| ------------------------- | ------- | ----------------------------------------------------- |
| `bcc31-toolchain.tar.enc` | `bcc31` | Borland C++ 3.1 `BCC.EXE` (DPMI-hosted)               |
| `tc20-toolchain.tar.enc`  | `tc20`  | Turbo C 2.0 `TCC.EXE`                                 |
| `wc10-toolchain.tar.enc`  | `wc10`  | Watcom C/C++ 10.0a `WCC.EXE` (16-bit; needs W32RUN)   |
| `msc6-toolchain.tar.enc`  | `msc6`  | Microsoft C 6.0 `CL.EXE` + `C1`/`C2`/`C3` passes      |

MS C **6.0** is used rather than 7.0: 7.0's `CL.EXE` is a DOSX32 image needing a
32-bit DPMI host DOSBox does not provide, so it cannot run under the harness.

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
staged before packing; `scripts/expand-szdd.py` is the same thing where PowerShell
is not available.

### pds70 and pds71: what actually went wrong

Both batteries skipped for over a year, and the recorded reason was wrong twice over.
The diagnosis said "staged from `BINB\` (OS/2) instead of `BIN\` (DOS)". Neither half
of that holds:

* **The PDS 7.x tools are BOUND executables.** There is exactly one `BC.EX$` on the
  media - no DOS and OS/2 variants to confuse - because a single file holds both
  builds. Its MZ part is the entire DOS program: `BC.EXE` 7.10 carries a 13.6 KB stub
  with the compiler's banner in it and runs under DOS perfectly well. Classifying it
  by its NE signature is what produced "OS/2 executable, not a DOS one".
* **The staged copies were corrupt, not the wrong build.** They were expanded with
  `expand-szdd` while it started the ring buffer at 4096-16 instead of 4096-18, which
  shifts every back-reference. The result has exactly the right length and a plausible
  header - the staged `BC.EXE` matched the correct one's 127,987 bytes to the byte -
  while 44% of its contents were wrong. That is why it did not print a complaint under
  DOS but crashed outright.

So the fix was the expander, not the media. Both toolchains are now staged from the
distribution disks with `scripts/stage-pds.sh`, and all four diff programs pass.

Telling a bound image from an OS/2-only one is done by the **size of the DOS stub** -
a real program versus a few hundred bytes that print a complaint. Searching for the
complaint does not work: the correct `BC.EXE` contains "will only work in Microsoft
Operating System/2 mode" as well, in its OS/2 half. That string is what sold the wrong
diagnosis in the first place.

```bash
scripts/stage-pds.sh pds71 ~/Downloads/<archive or disk images>
PB_TOOLCHAIN_KEY=... bash scripts/pack-toolchains.sh pds71
```

`pack-toolchains.sh` is the exact inverse of the harness's decrypt step, so a
container always round-trips. The raw `tools/<dialect>/` directories are
git-ignored; only the `.enc` tarballs are tracked. Each tarball's top level holds
the files the harness mounts (e.g. `PBC.EXE` for PB dialects; the `BC`/`LINK`
chain for QB via `oracle.conf`; the interpreter EXE via `oracle.interpreter`).
