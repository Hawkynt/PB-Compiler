' Differential battery 55: floating-point math vs genuine PBC 3.5.
' Arithmetic, SQR/ABS/SGN/INT/FIX, conversions and SINGLE vs DOUBLE formatting
' must print byte-identically (PB's number-formatting rules incl. the SINGLE/
' DOUBLE digit counts and the integral-float no-decimal-point rule).
OPEN "RESULT.TXT" FOR OUTPUT AS #1

a! = 3.5
b! = 2.0
PRINT #1, a! + b!; a! - b!; a! * b!; a! / b!
PRINT #1, SQR(2.0); SQR(16.0); ABS(-3.5); SGN(-7.25); SGN(0.0); SGN(4.0)
PRINT #1, INT(3.7); INT(-3.2); FIX(3.7); FIX(-3.2)

' DOUBLE precision
d# = 1.0 / 3.0
PRINT #1, d#
e# = 2.0 ^ 10.0
PRINT #1, e#; SQR(2.0#)

' conversions
PRINT #1, CINT(3.6); CINT(-3.6); CLNG(100000.4); CDBL(1)
f! = 100.0
PRINT #1, f! \ 7; INT(f! / 7.0)

' integral floats print without a decimal point (PB quirk)
g! = 5.0
PRINT #1, g!; g! * 2.0; 10.0 / 2.0

' comparisons
PRINT #1, (a! > b!); (a! = 3.5); (d# < 0.34)

CLOSE #1
END
