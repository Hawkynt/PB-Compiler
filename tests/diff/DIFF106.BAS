' Differential battery 106: O5 register residency across a PRINT (beyond the FOR shape).
' A PRINT of plain numeric items leaves SI and DI untouched - every print runtime routine
' (rt_print_i16/i32, rt_print_str, newline, zone, FSelect) preserves both index registers - so a
' hot accumulator and the FOR counter stay register-resident across a loop body that prints each
' iteration, instead of spilling to their memory cells. Register residency is output-invariant,
' so this stays byte-identical to genuine PBC 3.50 under pb35 and pb36.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' accumulator + counter resident across a per-iteration numeric PRINT
s% = 0
FOR i% = 1 TO 12
  s% = s% + i%
  PRINT #1, "step"; i%; s%
NEXT i%
PRINT #1, "sum"; s%

' a clean IF in the printing body still keeps the counter resident
t% = 0
FOR j% = 1 TO 10
  IF j% > 5 THEN t% = t% + j%
  PRINT #1, "t"; j%; t%
NEXT j%

CLOSE #1
END
