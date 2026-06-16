' Differential battery 74: C1 ($CPU 80386) DWORD-wide ERASE zero-fill.
' Under pb36 $OPTIMIZE SPEED + $CPU 80386 ERASE of a static array zeroes the block with
' REP STOSD (plus a trailing STOSW for an odd leftover word) instead of REP STOSW - same
' byte count, ~2x faster, identical (all-zero) result. Byte-identical to pb35 and PBC 3.50.
$CPU 80386
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

DIM a%(1 TO 10)
FOR i% = 1 TO 10
  a%(i%) = i% * 100
NEXT i%
PRINT #1, "before"; a%(1); a%(5); a%(10)
ERASE a%
PRINT #1, "after"; a%(1); a%(5); a%(10)

' refill and erase again
FOR i% = 1 TO 10
  a%(i%) = 7
NEXT i%
ERASE a%
PRINT #1, "after2"; a%(3); a%(7)

' 5 INTEGERs = 10 bytes: REP STOSD covers 8, the trailing STOSW the odd leftover word
DIM b%(1 TO 5)
FOR i% = 1 TO 5
  b%(i%) = i%
NEXT i%
ERASE b%
PRINT #1, "btail"; b%(1); b%(3); b%(5)

' a LONG array (DWORD-aligned, no tail)
DIM c&(1 TO 4)
FOR i% = 1 TO 4
  c&(i%) = i% * 1000000
NEXT i%
ERASE c&
PRINT #1, "clong"; c&(1); c&(4)

CLOSE #1
END
