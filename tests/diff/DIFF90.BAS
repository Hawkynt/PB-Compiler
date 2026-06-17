' Differential battery 90: O5 nested-loop register residency.
' Under pb36 $OPTIMIZE SPEED a doubly-nested INTEGER loop with SI/DI-clean bodies keeps the
' outer counter in SI and the inner counter in DI (the second and last safe index register).
' The inner counter is flushed to its cell on loop exit, so a post-loop read sees the genuine
' increment-then-test end value. All observable results must stay byte-identical to genuine
' PBC 3.50 under pb35 and pb36.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' plain double sum: s = sum over i,j of i*j  (multiply in body uses BX/CX/DX, not SI/DI)
s% = 0
FOR i% = 1 TO 6
  FOR j% = 1 TO 5
    s% = s% + i% * j%
  NEXT j%
NEXT i%
PRINT #1, "sum"; s%

' inner range references the outer counter; read the inner counter's end value after the loop
t% = 0
FOR i% = 1 TO 5
  FOR j% = i% TO 5
    t% = t% + j%
  NEXT j%
  PRINT #1, "jend"; i%; j%
NEXT i%
PRINT #1, "tri"; t%

' descending inner counter + EXIT FOR out of the inner loop
FOR i% = 1 TO 3
  FOR j% = 9 TO 1 STEP -1
    IF j% = 4 THEN EXIT FOR
    PRINT #1, "d"; i%; j%;
  NEXT j%
  PRINT #1, ""
NEXT i%

' triple nesting: only two index registers exist, so the optimizer must NOT mis-residency
' the third level - the result must still be correct
u% = 0
FOR i% = 1 TO 3
  FOR j% = 1 TO 3
    FOR k% = 1 TO 3
      u% = u% + 1
    NEXT k%
  NEXT j%
NEXT i%
PRINT #1, "cube"; u%

CLOSE #1
END
