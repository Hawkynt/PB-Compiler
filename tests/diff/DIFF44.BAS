' Differential battery 44: O8 int16 constant-operand folding into immediates.
' Bitwise masks (AND/OR/XOR) and comparisons against a constant fold the
' constant into an immediate ALU op; the constant may sit on either side, and
' an ordering compare with the constant on the left mirrors the operator.
' Signed INTEGER and unsigned WORD comparisons, plus the -1/0 truth values,
' must all match the genuine register-operand path.
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' ---- bitwise masks, constant on the right -----------------------------------
x% = 100
PRINT #1, x% AND 15; x% AND 12; x% OR 1; x% OR 128; x% XOR 255; x% XOR 6
' constant on the left (commutative)
PRINT #1, 15 AND x%; 1 OR x%; 255 XOR x%
' wrap / negative patterns
y% = -1
PRINT #1, y% AND 255; y% XOR 1; y% OR 0

' ---- signed INTEGER comparisons (truth value -1 / 0) ------------------------
a% = 50
PRINT #1, a% = 50; a% = 49; a% <> 50; a% < 100; a% > 10; a% <= 50; a% >= 51
' constant on the left -> mirrored operator
PRINT #1, 100 > a%; 10 < a%; 50 <= a%; 51 >= a%; 50 = a%
' negative operand signed ordering
n% = -5
PRINT #1, n% < 0; n% > -10; n% <= -5; -10 < n%

' ---- unsigned WORD comparisons (WORD constants keep the compare 16-bit) -----
u?? = 60000
PRINT #1, u?? > 50000??; u?? < 65000??; u?? = 60000??; 50000?? < u??; u?? >= 60000??

' ---- comparison against zero (OR AX,AX peephole) ---------------------------
z% = 0
p% = 7
m% = -7
PRINT #1, z% = 0; p% = 0; z% <> 0; p% <> 0
PRINT #1, p% > 0; m% > 0; m% < 0; p% < 0; z% >= 0; m% >= 0; z% <= 0; p% <= 0
PRINT #1, 0 = z%; 0 < p%; 0 > m%

' ---- compares inside IF (branch use, not value) ----------------------------
IF x% = 100 THEN PRINT #1, "eq100"
IF x% < 200 THEN PRINT #1, "lt200"
IF 5 < x% THEN PRINT #1, "5ltx"
IF x% AND 4 THEN PRINT #1, "bit2set"

CLOSE #1
END
