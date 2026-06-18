' Differential battery 104: O16 LONG \ / MOD type narrowing to a 16-bit IDIV.
' A signed LONG divide/remainder by a compile-time constant of magnitude 2..32767 whose
' dividend the interval lattice proves fits int16 runs as one 8086 IDIV instead of the
' 32-bit LongDiv/LongMod runtime call. The 16-bit IDIV truncates toward zero and the
' remainder takes the dividend's sign - exactly PB's \ and MOD - across positive, negative
' and sign-crossing dividends with positive and negative divisors. Byte-identical to genuine
' PBC 3.50 under pb35 and pb36.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' FOR-counter dividend (range pinned by the loop): divide and MOD, positive and negative divisor
FOR i& = -20 TO 20
  PRINT #1, "d"; i&; i& \ 3; i& MOD 3; i& \ -7; i& MOD -7
NEXT i&

' lattice-derived range (m& = k& + 100 with k& in [0,30] -> m& in [100,130], still int16)
FOR k& = 0 TO 30
  m& = k& + 100
  PRINT #1, "e"; m&; m& \ 5; m& MOD 5
NEXT k&

CLOSE #1
END
