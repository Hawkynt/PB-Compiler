' Differential battery 109: O5 register residency across a SELECT CASE (beyond the FOR shape).
' An INTEGER SELECT CASE dispatches through AX/BX/DX (its word jump table - MOV BX / SHL / indexed
' JMP - or the AX/BX compare chain) and never the index registers, so a hot accumulator and the FOR
' counter stay register-resident across a loop body that branches through a SELECT, instead of
' spilling to their cells. Register residency is output-invariant, so byte-identical to genuine
' PBC 3.50 under pb35 and pb36.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

s% = 0
FOR i% = 1 TO 15
  SELECT CASE i%
    CASE 1, 3, 5, 7
      s% = s% + i%
    CASE 8 TO 11
      s% = s% + 100
    CASE ELSE
      s% = s% - 1
  END SELECT
  PRINT #1, "i"; i%; s%
NEXT i%
PRINT #1, "sum"; s%

CLOSE #1
END
