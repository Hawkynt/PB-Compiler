' Differential battery 81: O16 range lattice over strided counter expressions (counter*const).
' IndexRangeOf now scales a FOR-counter range by a constant, so a(i*2), comparisons of i*2,
' and i*2 divisors all get the range-lattice treatment. Byte-identical to pb35 and PBC 3.50.
$ERROR BOUNDS ON
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1
DIM a%(0 TO 8)

' strided store/read: i*2 over i in [0,4] -> [0,8], inside the bounds -> checks elided
FOR i% = 0 TO 4
  a%(i% * 2) = i% * 10
NEXT i%
s% = 0
FOR i% = 0 TO 4
  s% = s% + a%(i% * 2)
NEXT i%
PRINT #1, "stride"; s%; a%(0); a%(8)

' strided comparison fold (no $ERROR needed): 2*i < 30 over i in [1,10] -> always true
u% = 0
FOR i% = 1 TO 10
  IF 2 * i% < 30 THEN u% = u% + 1
NEXT i%
PRINT #1, "cmp"; u%

' strided divisor that excludes zero: 200 \ (i*2) over i in [1,10] -> divisor [2,20]
d% = 0
FOR i% = 1 TO 10
  d% = d% + (200 \ (i% * 2))
NEXT i%
PRINT #1, "divs"; d%

CLOSE #1
END
