' Differential battery 77: O16 FOR-counter-range bounds-check elimination.
' Under pb36 $OPTIMIZE SPEED + $ERROR BOUNDS ON, an array index that is exactly a FOR
' counter whose constant [From,To] range lies inside the array's static bounds can never
' raise Error 9, so the per-iteration bounds check is dropped. The check passing (genuine)
' and the check being absent (ours) produce identical output - no error either way.
$ERROR BOUNDS ON
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1
DIM a%(1 TO 10)

' store through the counter (index in bounds) - store-side check elided
FOR i% = 1 TO 10
  a%(i%) = i% * 3
NEXT i%

' read through the counter (index in bounds) - read-side check elided
s% = 0
FOR i% = 1 TO 10
  s% = s% + a%(i%)
NEXT i%
PRINT #1, "sum"; s%

' a sub-range of the array (still in bounds) and a descending loop
DIM b%(0 TO 20)
FOR i% = 5 TO 15
  b%(i%) = i%
NEXT i%
t% = 0
FOR i% = 15 TO 5 STEP -1
  t% = t% + b%(i%)
NEXT i%
PRINT #1, "sub"; t%

' a NON-counter variable index keeps its check (still in bounds, no error)
j% = 4
PRINT #1, "var"; a%(j%)

' affine counter indices a(i-1)/a(i+1): i in [2,9] -> i-1 in [1,8], i+1 in [3,10],
' both inside [1,10], so both checks elide (neighbour access)
DIM c%(1 TO 10)
FOR i% = 1 TO 10
  c%(i%) = i% * i%
NEXT i%
u% = 0
FOR i% = 2 TO 9
  u% = u% + c%(i% - 1) + c%(i% + 1)
NEXT i%
PRINT #1, "affine"; u%

CLOSE #1
END
