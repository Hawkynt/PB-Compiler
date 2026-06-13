' Differential battery 36: O20 array-copy idiom (dst(i)=src(i) -> REP MOVSW)
' must match the element-by-element loop and the counter end value.
$OPTIMIZE SPEED
DIM a%(0 TO 50), b%(0 TO 50)
OPEN "RESULT.TXT" FOR OUTPUT AS #1
FOR i% = 0 TO 50
  a%(i%) = i% * 3 - 10
NEXT i%
FOR i% = 5 TO 40
  b%(i%) = a%(i%)
NEXT i%
PRINT #1, b%(4); b%(5); b%(20); b%(40); b%(41); i%
' descending or mismatched-array copy stays correct
FOR j% = 0 TO 50
  b%(j%) = 0
NEXT j%
FOR j% = 10 TO 12
  b%(j%) = a%(j%)
NEXT j%
PRINT #1, b%(9); b%(10); b%(11); b%(12); b%(13); j%
CLOSE #1
END
