' Differential battery 64: induction-variable strength reduction for array element reads.
' Under pb36 $OPTIMIZE SPEED, a FOR loop whose single-statement body reads a static
' INTEGER array by the loop counter (x% = a%(i%)) steps a pointer instead of recomputing
' base + i*size each iteration; the observable values must be byte-identical to pb35.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1
DIM a%(5 TO 25)
FOR i% = 5 TO 25
  a%(i%) = (i% - 5) * 7 - 3
NEXT i%
x% = 0
FOR i% = 5 TO 25
  x% = a%(i%)
NEXT i%
PRINT #1, "fwd"; x%
FOR i% = 25 TO 5 STEP -1
  x% = a%(i%)
NEXT i%
PRINT #1, "rev"; x%
FOR i% = 5 TO 25 STEP 3
  x% = a%(i%)
NEXT i%
PRINT #1, "step3"; x%
FOR i% = 25 TO 5 STEP -4
  x% = a%(i%)
NEXT i%
PRINT #1, "rev4"; x%
FOR i% = 10 TO 5
  x% = a%(i%)
NEXT i%
PRINT #1, "empty"; x%
CLOSE #1
END
