' Differential battery 99: O16 interval lattice wired into IndexRangeOf.
' The lattice proves ranges for arbitrary (non-FOR-counter) variables, so a lattice-bounded
' array index drops its Error-9 check and a lattice-bounded comparison can fold - but only when
' SOUND. The lattice is wrap-correct: a value that overflows its type reads as unknown, so a
' comparison over a wrapped value is NOT folded. All results must stay byte-identical to genuine
' PBC 3.50 under pb35 and pb36.
$ERROR BOUNDS ON
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' a lattice-bounded index (n%+m%+i% = 10+i% in [11,14], inside a%(0 TO 20)) - check drops, value same
DIM a%(0 TO 20)
n% = 3
m% = 7
FOR i% = 1 TO 4
  k% = n% + m% + i%
  a%(k%) = k%
NEXT i%
PRINT #1, "idx"; a%(11); a%(12); a%(13); a%(14)

' branch fold over a lattice-bounded variable: p% in [5,8] is always < 20
p% = 5
IF q% > 0 THEN p% = 8
IF p% < 20 THEN
  PRINT #1, "lt yes"; p%
ELSE
  PRINT #1, "lt no"; p%
END IF

' WRAP GUARD: w% overflows INTEGER (30000+30000 = 60000 -> -5536). The lattice must read w% as
' unknown, so the comparison is NOT folded - the real (wrapped) value decides.
w% = 30000
w% = w% + 30000
IF w% < 0 THEN
  PRINT #1, "wrap neg"; w%
ELSE
  PRINT #1, "wrap pos"; w%
END IF

' a LONG holds the same sum without wrap; its lattice range is honest
v& = 30000
v& = v& + 30000
IF v& > 50000 THEN
  PRINT #1, "long big"; v&
ELSE
  PRINT #1, "long small"; v&
END IF

CLOSE #1
END
