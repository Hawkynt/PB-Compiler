' Differential battery 75: C1 ($CPU 80386) DWORD-wide constant array fill.
' A FOR loop that stores a constant into every element of an INTEGER array is lowered to a
' REP STOSW; under pb36 $OPTIMIZE SPEED + $CPU 80386 the 16-bit value is broadcast into
' both halves of EAX and stored two elements per REP STOSD, with a trailing STOSW for an
' odd count - same words written, ~2x faster. Byte-identical to pb35 and genuine PBC 3.50.
$CPU 80386
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

DIM a%(1 TO 10)
FOR i% = 1 TO 10
  a%(i%) = 1234
NEXT i%
PRINT #1, "fill"; a%(1); a%(5); a%(10)

' odd element count -> the trailing STOSW covers the last word
DIM b%(1 TO 7)
FOR i% = 1 TO 7
  b%(i%) = -5
NEXT i%
PRINT #1, "odd"; b%(1); b%(4); b%(7)

' negative fill value: the broadcast must replicate the high bit into both halves
DIM c%(0 TO 8)
FOR i% = 0 TO 8
  c%(i%) = -1
NEXT i%
PRINT #1, "neg"; c%(0); c%(4); c%(8)

CLOSE #1
END
