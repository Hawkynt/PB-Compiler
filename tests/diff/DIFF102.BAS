' Differential battery 102: O16 lattice guard refinement.
' Inside an IF arm the lattice refines a variable's range by the (possibly negated) comparison, so
' a guarded array index whose UNREFINED range exceeds the array bounds but whose GUARDED range fits
' drops its Error-9 check under $ERROR BOUNDS ON. The guard ensures the access is in range, so the
' result is identical to genuine PBC 3.50 under pb35 and pb36.
$ERROR BOUNDS ON
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' k% in [7,100] exceeds a%(0 TO 20), but the guard k% <= 20 refines it to [7,20] -> in bounds
DIM a%(0 TO 20)
k% = 7
IF q% > 0 THEN k% = 100
IF k% <= 20 THEN
  a%(k%) = k%
END IF
PRINT #1, "guard"; a%(7)

' the ELSE complement: m% in [0,40], the THEN handles m% < 21, the ELSE sees m% >= 21
DIM b%(21 TO 40)
m% = 5
IF r% > 0 THEN m% = 30
IF m% < 21 THEN
  PRINT #1, "lo"; m%
ELSE
  b%(m%) = m%
  PRINT #1, "hi"; b%(30)
END IF

' a lower-bound guard combines with a constant upper to bound both ends
DIM c%(0 TO 50)
n% = 8
IF s% > 0 THEN n% = -5
IF n% >= 0 THEN
  c%(n%) = n% * 2
  PRINT #1, "pos"; c%(8)
END IF

CLOSE #1
END
