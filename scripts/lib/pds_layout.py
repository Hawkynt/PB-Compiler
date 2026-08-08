#!/usr/bin/env python3
"""Lay expanded PDS 7.x media out as the BC7/BIN + BC7/LIB tree the oracle wants.

    pds_layout.py <expanded-flat-dir> <target BC7 dir>

Called by scripts/stage-pds.sh. Every input name carries its source directory as a
prefix (``BIN_BC.EXE``, ``BINB_BC.EXE``), so the same tool arriving from two places
cannot collide before it has been classified.
"""
import os
import shutil
import struct
import sys

TOOLS = ("BC", "LINK", "LIB")


def kind(path):
    """Whether this executable runs under DOS, decided by its DOS stub.

    An NE header does NOT mean "cannot run under DOS". The PDS 7.x tools are BOUND
    executables: one file holds both builds, and the MZ part is the entire DOS
    program - BC.EXE 7.10 carries a 13.6 KB stub with the compiler's banner in it and
    runs under DOS perfectly well. A genuinely OS/2-only image instead has a stub of
    a few hundred bytes whose only job is to print a complaint, so the stub's SIZE is
    what separates the two.

    Searching the file for that complaint does not work: the correctly expanded
    BC.EXE contains "will only work in Microsoft Operating System/2 mode" as well, in
    its OS/2 half. That string is what sold the wrong diagnosis for this toolchain.
    """
    with open(path, "rb") as handle:
        data = handle.read()
    if data[:8] == b"SZ \x88\xf0'3\xd1":
        return "still-compressed", data
    if data[:2] != b"MZ" or len(data) < 0x40:
        return "not-an-executable", data
    lfanew = struct.unpack("<I", data[0x3C:0x40])[0]
    if not 0 < lfanew < len(data) - 2:
        return "DOS", data
    signature = data[lfanew:lfanew + 2]
    if signature not in (b"NE", b"PE", b"LE", b"LX"):
        return "DOS", data
    pages, last = struct.unpack("<H", data[4:6])[0], struct.unpack("<H", data[2:4])[0]
    stub = (pages - 1) * 512 + last if pages else 0
    if stub >= 4096:
        return "DOS", data                      # bound: the stub is a real program
    if signature == b"NE" and data[lfanew + 54] == 1:
        return "OS/2", data
    return signature.decode(), data


def main(argv):
    source, target = argv[1], argv[2]
    tools, members, rejected = {}, {}, []

    for name in sorted(os.listdir(source)):
        path = os.path.join(source, name)
        if not os.path.isfile(path):
            continue
        upper = name.upper()
        bare = upper.rsplit("_", 1)[-1]

        tool = next((t for t in TOOLS if bare == t + ".EXE"), None)
        if tool is not None:
            verdict, data = kind(path)
            if verdict != "DOS":
                rejected.append((name, verdict))
            elif tool not in tools or len(data) > tools[tool][1]:
                # The largest DOS build wins: a disk set may also carry a small
                # loader of the same name next to the tool proper.
                tools[tool] = (path, len(data))
            continue

        if bare.endswith((".LIB", ".OBJ")):
            # A member can sit on several disks; keep the largest copy of each name.
            size = os.path.getsize(path)
            if bare not in members or size > members[bare][1]:
                members[bare] = (path, size)

    for tool, (path, size) in sorted(tools.items()):
        shutil.copyfile(path, os.path.join(target, "BIN", tool + ".EXE"))
        print(f"  BIN/{tool + '.EXE':9s} {size:>8} bytes")
    for bare, (path, _) in sorted(members.items()):
        shutil.copyfile(path, os.path.join(target, "LIB", bare))
    print(f"  LIB/ {len(members)} libraries and objects")
    for name, verdict in rejected:
        print(f"  skipped {name} ({verdict})")


if __name__ == "__main__":
    main(sys.argv)
