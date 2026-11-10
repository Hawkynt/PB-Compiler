#!/usr/bin/env bash
set -euo pipefail

# Standalone independent VGA implementation oracle. QEMU and NASM are
# test-time tools only; neither is linked into or shipped with PB-Compiler.
command -v nasm >/dev/null || { echo 'nasm is required for the QEMU VGA oracle' >&2; exit 2; }
command -v qemu-system-i386 >/dev/null || { echo 'qemu-system-i386 is required' >&2; exit 2; }

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

nasm -f bin "$repo/tests/oracles/vga-qemu-boot.asm" -o "$tmp/boot.bin"
test "$(wc -c < "$tmp/boot.bin")" -eq 512
truncate -s 1474560 "$tmp/vga.img"
dd if="$tmp/boot.bin" of="$tmp/vga.img" conv=notrunc status=none

set +e
timeout 30s qemu-system-i386 \
  -machine pc,accel=tcg -cpu 486 -m 16M \
  -display none -monitor none -serial none -parallel none \
  -drive "file=$tmp/vga.img,format=raw,if=floppy" -boot a \
  -no-reboot -debugcon "file:$tmp/result.txt" \
  -global isa-debugcon.iobase=0xe9 \
  -device isa-debug-exit,iobase=0xf4,iosize=0x04 \
  >"$tmp/qemu.log" 2>&1
status=$?
set -e

if [[ "$status" -ne 1 ]]; then
  echo "QEMU did not reach the controlled VGA oracle exit (status $status)" >&2
  cat "$tmp/qemu.log" >&2
  [[ ! -e "$tmp/result.txt" ]] || cat "$tmp/result.txt" >&2
  exit 1
fi

expected="6565656555AAFF00AA55"
actual="$(tr -d '\r\n' < "$tmp/result.txt")"
if [[ "$actual" != "$expected" ]]; then
  printf 'Unexpected QEMU VGA planes / BIOS pixels:\n%s\nExpected:\n%s\n' "$actual" "$expected" >&2
  exit 1
fi
echo "QEMU independent VGA + BIOS truth table verified: $actual"
