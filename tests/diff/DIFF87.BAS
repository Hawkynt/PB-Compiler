' Differential battery 87: O16 FOR-counter-range 32-bit (LONG) overflow-check elimination.
' Under pb36 $OPTIMIZE SPEED + $ERROR OVERFLOW ON, a LONG add/subtract whose affine FOR-counter
' range provably stays inside the signed 32-bit range can never raise Error 6, so the 32-bit
' ADD/ADC (SUB/SBB) overflow check is dropped. The check passing (genuine) and being absent
' (ours) give identical output - no error either way. Byte-identical to pb35 and PBC 3.50.
$ERROR OVERFLOW ON
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' i& + 1& over [1,100] -> [2,101], fits 32 bits -> inner LONG add check dropped
s& = 0
FOR i& = 1 TO 100
  s& = s& + (i& + 1&)
NEXT i&
PRINT #1, "add1"; s&

' i& - 1& over [1,100] -> [0,99], fits 32 bits -> inner LONG sub check dropped
d& = 0
FOR i& = 1 TO 100
  d& = d& + (i& - 1&)
NEXT i&
PRINT #1, "sub1"; d&

' a wide affine LONG shift that still fits inside 32 bits
w& = 0
FOR i& = 1000000 TO 1000050
  w& = w& + (i& - 999000&)
NEXT i&
PRINT #1, "wide"; w&

CLOSE #1
END
