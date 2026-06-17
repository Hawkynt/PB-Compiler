' Differential battery 93: O16 integer-divide-by-constant range bounds-check elimination.
' IndexRangeOf now bounds `x \ k` (k a constant) by dividing the endpoints (truncated divide
' is monotonic in the dividend), so binary-heap / tree style indexing a(i \ 2) whose range lies
' inside the array drops its Error-9 check under $ERROR BOUNDS ON. Output is identical either
' way, so this must stay byte-identical to genuine PBC 3.50 under pb35 and pb36.
$ERROR BOUNDS ON
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' binary-heap parent index: i% \ 2 over [0,30] is in [0,15]
DIM h%(0 TO 15)
FOR i% = 0 TO 30
  h%(i% \ 2) = i%
NEXT i%
PRINT #1, "half"; h%(0); h%(8); h%(15)

' divide by 3 over a non-negative counter: i% \ 3 over [0,29] is in [0,9]
DIM t%(0 TO 9)
FOR i% = 0 TO 29
  t%(i% \ 3) = t%(i% \ 3) + 1
NEXT i%
PRINT #1, "third"; t%(0); t%(5); t%(9)

' affine then divide: (i% + 4) \ 2 over [0,20] -> [2,12]
DIM a%(0 TO 12)
FOR i% = 0 TO 20
  a%((i% + 4) \ 2) = i%
NEXT i%
PRINT #1, "aff"; a%(2); a%(12)

CLOSE #1
END
