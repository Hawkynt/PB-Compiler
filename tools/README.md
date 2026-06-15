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
| `tb11-toolchain.tar.enc` | `tb11` | Turbo Basic 1.1 `TB.EXE` |

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

## Adding / re-packing a dialect

```bash
# collect the compiler + the runtime files it needs into a directory, then:
tar cz -C tools/<dialect> . \
  | openssl enc -aes-256-cbc -pbkdf2 -salt \
      -pass env:PB_TOOLCHAIN_KEY -out tools/<dialect>-toolchain.tar.enc
```

The raw `tools/<dialect>/` directories are git-ignored; only the `.enc`
tarballs are tracked. Each tarball's top level holds the files the harness
mounts (e.g. `PBC.EXE` for PB dialects; the `BC`/`LINK` chain for QB, driven by
`tests/diff/<dialect>/oracle.conf`).
