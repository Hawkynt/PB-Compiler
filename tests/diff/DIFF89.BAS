' Differential battery 89: O16 two-range index bounds-check elimination.
' IndexRangeOf now adds/subtracts two range-known operands (a FOR counter plus a derived
' range-known variable), so an index like a(i+j) or a(i-j) whose summed/differenced range
' lies inside the array's static bounds drops its Error-9 check under $ERROR BOUNDS ON. The
' check passing (genuine) and being absent (ours) produce identical output - no error either
' way - so this must stay byte-identical to genuine PBC 3.50 under pb35 and pb36.
$ERROR BOUNDS ON
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' sum of two range-known vars: i in [2,9], j = i-1 in [1,8], i+j in [3,17] <= 30
DIM a%(0 TO 30)
FOR i% = 2 TO 9
  j% = i% - 1
  a%(i% + j%) = i% * 10 + j%
NEXT i%
PRINT #1, "sum"; a%(3); a%(10); a%(17)

' difference of two range-known vars: i in [1,8], k = i+2 in [3,10], k-i = 2 (range [3-8,10-1]=[-5,9])
' the access uses (k% - i% + 5) to stay non-negative: range [0,14] <= 20
DIM b%(0 TO 20)
FOR i% = 1 TO 8
  k% = i% + 2
  b%(k% - i% + 5) = i%
NEXT i%
PRINT #1, "dif"; b%(7)

' affine still works (regression guard): a(i-1), a(i+1)
DIM c%(0 TO 12)
FOR i% = 1 TO 10
  c%(i% - 1) = i%
  c%(i% + 1) = i% * 2
NEXT i%
PRINT #1, "aff"; c%(0); c%(11)

CLOSE #1
END
