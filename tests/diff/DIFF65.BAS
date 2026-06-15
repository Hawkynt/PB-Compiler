' Differential battery 65: induction-variable strength reduction for array element STORES.
' Under pb36 $OPTIMIZE SPEED, a FOR loop whose single-statement body stores into a static
' INTEGER array by the loop counter (a%(i%) = expr) steps a pointer instead of recomputing
' the address; the stored values must be byte-identical to pb35.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1
DIM a%(5 TO 25)
FOR i% = 5 TO 25
  a%(i%) = (i% - 5) * 7 - 100
NEXT i%
FOR i% = 25 TO 5 STEP -2
  a%(i%) = i% + 1000
NEXT i%
t& = 0
FOR i% = 5 TO 25
  t& = t& + a%(i%)
NEXT i%
PRINT #1, "sum"; t&
PRINT #1, a%(5); a%(12); a%(13); a%(25)
CLOSE #1
END
