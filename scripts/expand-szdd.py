#!/usr/bin/env python3
"""expand-szdd.py - decompress MS "SZ " (old SZDD / install-media) LZSS files.

The portable twin of expand-szdd.ps1, for staging toolchains where PowerShell is
not available. Vintage Microsoft install disks (MS BASIC PDS 7.x among them) store
files compressed with the old SZDD variant "SZ\\x20\\x88\\xF0\\x27\\x33\\xD1", which
neither 7-Zip nor the modern Windows expand.exe decodes.

Format: 8-byte magic, 4-byte uncompressed length (LE) at offset 8, then an LZSS
stream over a 4096-byte ring buffer pre-filled with spaces. Control byte read
LSB-first: a set bit is a literal, a clear bit a (12-bit offset, 4-bit length)
back-reference whose length is the stored nibble + 3.

    expand-szdd.py <file.in> <file.out>    # one file
    expand-szdd.py <dir.in>  <dir.out>     # a directory: SZDD members expanded
                                           # (.EX$/.LI$/.OB$/.HL$ -> .EXE/.LIB/
                                           # .OBJ/.HLP), everything else copied
"""
import os
import shutil
import struct
import sys

# The ring buffer starts at N - F, and F is fixed by the format's own encoding:
# a match length is a 4-bit nibble plus 3, so the longest is 18, so F is 18 and
# the start is 4096 - 18. Getting this wrong does NOT fail loudly - the output is
# still exactly the declared length and still starts with a plausible header,
# because only back-references are shifted. It is simply wrong from the first
# match onward: with 4080 (F=16, the NEWER SZDD format's value) roughly 44% of
# the bytes of a PDS 7.0 BC.EXE come out corrupted, and the give-away is that the
# readable strings turn to noise while the file size stays perfect.
_WINDOW = 4096
_START = _WINDOW - 18

_MAGIC = b"SZ \x88\xf0'3\xd1"


def expand(data):
    """The expanded bytes, or None when this is not an old-SZDD file."""
    if len(data) < 12 or data[:8] != _MAGIC:
        return None
    want = struct.unpack("<I", data[8:12])[0]
    window = bytearray(b" " * _WINDOW)
    out = bytearray()
    wpos = _START
    i = 12
    while i < len(data) and len(out) < want:
        control = data[i]
        i += 1
        for bit in range(8):
            if len(out) >= want or i >= len(data):
                break
            if control & (1 << bit):
                byte = data[i]
                i += 1
                out.append(byte)
                window[wpos] = byte
                wpos = (wpos + 1) % _WINDOW
            else:
                if i + 1 >= len(data):
                    break
                low, high = data[i], data[i + 1]
                i += 2
                match = low | ((high & 0xF0) << 4)
                for _ in range((high & 0x0F) + 3):
                    if len(out) >= want:
                        break
                    byte = window[match]
                    out.append(byte)
                    window[wpos] = byte
                    wpos = (wpos + 1) % _WINDOW
                    match = (match + 1) % _WINDOW
    return bytes(out)


def expanded_name(name):
    """A compressed member's name with its truncated last character restored.

    Microsoft used both spellings for the same convention - the older disks mark a
    compressed file with a trailing '$' and the later ones with '_'. Handling only
    '$' does not fail, it just quietly leaves the file under a name nothing looks
    for afterwards, which is the same class of silent miss as the rest of this.
    """
    upper = name.upper()
    for stem, full in (("EX", ".EXE"), ("LI", ".LIB"), ("OB", ".OBJ"), ("HL", ".HLP")):
        for mark in ("$", "_"):
            if upper.endswith("." + stem + mark):
                return name[: -(len(stem) + 2)] + full
    return name[:-1] + "_" if name[-1:] in ("$", "_") else name


def main(argv):
    if len(argv) != 3:
        sys.exit(__doc__)
    src, dst = argv[1], argv[2]

    if not os.path.isdir(src):
        out = expand(open(src, "rb").read())
        if out is None:
            sys.exit(f"{src} is not an old-SZDD 'SZ ' file")
        open(dst, "wb").write(out)
        print(f"{os.path.basename(src)} -> {os.path.basename(dst)} ({len(out)} bytes)")
        return

    os.makedirs(dst, exist_ok=True)
    count = 0
    for entry in sorted(os.listdir(src)):
        path = os.path.join(src, entry)
        if not os.path.isfile(path):
            continue
        out = expand(open(path, "rb").read())
        if out is None:
            shutil.copy2(path, os.path.join(dst, entry))
        else:
            open(os.path.join(dst, expanded_name(entry)), "wb").write(out)
            count += 1
    print(f"expanded {count} files into {dst}")


if __name__ == "__main__":
    main(sys.argv)
