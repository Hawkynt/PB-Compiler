' Differential battery 79: O16 FOR-counter-range overflow-check elimination.
' Under pb36 $OPTIMIZE SPEED + $ERROR OVERFLOW ON, an INTEGER add/subtract whose affine
' FOR-counter range provably stays inside 16 bits can never raise Error 6, so the overflow
' check is dropped. The check passing (genuine) and being absent (ours) give identical
' output - no error either way. Byte-identical to pb35 and genuine PBC 3.50.
$ERROR OVERFLOW ON
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' i%+1 over [1,100] -> [2,101], no overflow -> inner check dropped
s% = 0
FOR i% = 1 TO 100
  s% = s% + (i% + 1)
NEXT i%
PRINT #1, "add1"; s%

' i%-1 over [1,100] -> [0,99]
d% = 0
FOR i% = 1 TO 100
  d% = d% + (i% - 1)
NEXT i%
PRINT #1, "sub1"; d%

' a high counter range whose affine shift keeps it inside 16 bits
s2& = 0
FOR i% = 30000 TO 30100
  s2& = s2& + (i% - 100)
NEXT i%
PRINT #1, "wide"; s2&

CLOSE #1
END
