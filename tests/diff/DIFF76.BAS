' Differential battery 76: O6b induction-variable strength reduction for LONG array stores.
' Under pb36 $OPTIMIZE SPEED a FOR loop whose single body statement stores into a static
' rank-1 LONG array by the counter (a&(i%) = expr) steps a DS-relative pointer by 4 bytes
' per iteration (two shift-by-1 for the *4 offset, DX:AX stored as two words) instead of
' recomputing base + (i-lbound)*4. Observable values must be byte-identical to pb35 and PBC.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1
DIM a&(1 TO 10)

FOR i% = 1 TO 10
  a&(i%) = i% * 100000
NEXT i%
PRINT #1, "fwd"; a&(1); a&(5); a&(10)

FOR i% = 10 TO 1 STEP -1
  a&(i%) = i% * 7 - 3
NEXT i%
PRINT #1, "rev"; a&(1); a&(10)

FOR i% = 1 TO 10 STEP 2
  a&(i%) = -i% * 1000000
NEXT i%
PRINT #1, "step2"; a&(1); a&(3); a&(9)

' a LONG value beyond 16 bits, negative
DIM b&(0 TO 5)
FOR i% = 0 TO 5
  b&(i%) = (i% - 3) * 500000000
NEXT i%
PRINT #1, "wide"; b&(0); b&(3); b&(5)

' LONG array READ via the stepped pointer (x& = a&(i%))
DIM x AS LONG
FOR i% = 1 TO 10
  x = a&(i%)
NEXT i%
PRINT #1, "rdfwd"; x
FOR i% = 5 TO 1 STEP -2
  x = a&(i%)
NEXT i%
PRINT #1, "rdrev"; x
FOR i% = 0 TO 5
  x = b&(i%)
NEXT i%
PRINT #1, "rdwide"; x

CLOSE #1
END
