' Differential battery 110: O5 / C1 ($CPU 80386) - a LONG FOR counter lives in ESI.
' Under $CPU 80386 a LONG FOR counter over an SI-clean body is kept in the 32-bit register ESI for
' the loop: the compare and increment run as one 32-bit instruction (CMP ESI,[limit] / ADD ESI,step)
' and counter reads split ESI into DX:AX, so the per-iteration 4-byte cell load/store/reload
' disappears. Genuine PBC 3.50 accepts $CPU 80386 and emits its own correct code, and register
' residency is output-invariant, so this is byte-identical to genuine PBC 3.50 under pb35 and pb36.
$CPU 80386
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' ascending LONG counter, accumulator stays in its cell, counter resident in ESI
s& = 0
FOR i& = 1 TO 20
  s& = s& + i& * 100
  PRINT #1, "i"; i&; s&
NEXT i&
PRINT #1, "sum"; s&

' sign-crossing counter, reads materialised from ESI
FOR j& = -6 TO 6
  PRINT #1, "j"; j&; j& * j&
NEXT j&

' descending counter (negative step)
FOR k& = 10 TO 1 STEP -2
  PRINT #1, "k"; k&
NEXT k&

CLOSE #1
END
