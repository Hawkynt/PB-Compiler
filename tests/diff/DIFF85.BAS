' Differential battery 85: QUAD (64-bit integer) operators.
OPEN "RESULT.TXT" FOR OUTPUT AS #1
DIM a AS QUAD, b AS QUAD, c AS QUAD
a = 5000000000
b = 3
PRINT #1, a + b
PRINT #1, a - b
PRINT #1, a * b
PRINT #1, a \ b
PRINT #1, a MOD b
PRINT #1, a AND b
PRINT #1, a OR b
PRINT #1, a XOR b
PRINT #1, a EQV b
PRINT #1, a IMP b
PRINT #1, (a > b)
PRINT #1, (a = b)
PRINT #1, (a <> b)
PRINT #1, (a >= a)
PRINT #1, (b <= a)
PRINT #1, a / b
PRINT #1, a ^ 2
c = -a
PRINT #1, c
PRINT #1, NOT a
' negative operands and 64-bit boundaries
a = -9000000000000000000
b = 1000000000000000000
PRINT #1, a + b
PRINT #1, a \ b
PRINT #1, a MOD b
CLOSE #1
END
