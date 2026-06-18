' Differential battery 100: O16 interval lattice inside PROCEDURE bodies.
' The lattice now analyzes each SUB/FUNCTION body (parameters start unknown), so a lattice-bounded
' index, a branch fold, and the loop-condition handling all apply inside procedures - and the wrap
' guard still holds. All results must stay byte-identical to genuine PBC 3.50 under pb35 and pb36.
$ERROR BOUNDS ON
$OPTIMIZE SPEED
DECLARE SUB Work()
DECLARE FUNCTION Clamp%(BYVAL n AS INTEGER)
OPEN "RESULT.TXT" FOR OUTPUT AS #1

Work
PRINT #1, "clamp"; Clamp%(3); Clamp%(-9); Clamp%(99)

CLOSE #1
END

SUB Work()
  ' lattice-bounded index inside a SUB (k% in [5,10] inside b%(0 TO 20))
  DIM b%(0 TO 20)
  k% = 5
  IF z% > 0 THEN k% = 10
  b%(k%) = k%
  PRINT #1, "idx"; b%(5); b%(10)

  ' a DO loop in a SUB - the loop condition must see the invariant (no infinite loop)
  c% = 0
  DO WHILE c% < 6
    INCR c%
  LOOP
  PRINT #1, "do"; c%

  ' wrap guard inside a SUB: w% overflows INTEGER, the comparison uses the wrapped value
  w% = 30000
  w% = w% + 30000
  IF w% < 0 THEN
    PRINT #1, "wrap neg"; w%
  ELSE
    PRINT #1, "wrap pos"; w%
  END IF
END SUB

FUNCTION Clamp%(BYVAL n AS INTEGER)
  ' a parameter is unknown to the lattice, so the comparisons are not folded - real value decides
  IF n < 0 THEN
    Clamp% = 0
  ELSEIF n > 20 THEN
    Clamp% = 20
  ELSE
    Clamp% = n
  END IF
END FUNCTION
