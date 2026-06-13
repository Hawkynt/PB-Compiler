' Differential battery 39: O5 register-resident FOR counter must be invisible.
' An SI-clean body (scalar integer assignments reading the counter) keeps the
' counter in SI; counter reads, end value and results must match the cell loop.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1
acc% = 0
sq% = 0
FOR i% = 1 TO 8
  acc% = acc% + i%
  sq% = sq% + i% * i%
NEXT i%
PRINT #1, acc%; sq%; i%
' descending step, counter used in arithmetic
d% = 0
FOR j% = 20 TO 5 STEP -3
  d% = d% - j%
NEXT j%
PRINT #1, d%; j%
' long accumulator reading the int counter
t& = 0
FOR k% = 1 TO 100
  t& = t& + k% * 10
NEXT k%
PRINT #1, t&; k%
' counter read after the loop (must be the increment-then-test end value)
FOR m% = 1 TO 3
  c% = m%
NEXT m%
PRINT #1, c%; m%
' nested loops: only the inner is SI-clean, outer has a (call-free) inner loop
s% = 0
FOR a% = 1 TO 3
  FOR b% = 1 TO 3
    s% = s% + a% * b%
  NEXT b%
NEXT a%
PRINT #1, s%; a%; b%
CLOSE #1
END
