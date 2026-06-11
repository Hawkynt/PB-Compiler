' Differential battery 16: FIX (@) / BCD (@@) storage, arithmetic, printing
' and conversions (CFIX/CBCD/CQUD). Verified observations: pbvFixDigits
' defaults to 2; FIX literals round decimally (2.555 -> 2.56); arithmetic
' promotes to EXT; BCD carries the binary noise of its source values.
OPEN "RESULT.TXT" FOR OUTPUT AS #1

PRINT #1, pbvFixDigits

f@ = 1.23456
PRINT #1, f@
PRINT #1, STR$(f@)
f@ = 2.555
PRINT #1, f@
f@ = -2.555
PRINT #1, f@

g@ = 10
h@ = 3
PRINT #1, g@ / h@
PRINT #1, g@ * h@
PRINT #1, g@ + h@
PRINT #1, g@ - h@

b@@ = 1.5
c@@ = 0.0625
PRINT #1, b@@ + c@@
PRINT #1, b@@ * c@@
PRINT #1, b@@ / c@@
PRINT #1, b@@ - c@@
PRINT #1, SIZEOF(f@); SIZEOF(b@@)
IF g@ > h@ THEN PRINT #1, "gt"
IF c@@ < b@@ THEN PRINT #1, "lt"

PRINT #1, CFIX(2.7)
PRINT #1, CBCD(2.7)
PRINT #1, CQUD(1.9)
i% = f@
PRINT #1, i%

' FIX stores from computed values round at the fixed precision
k@ = 100
k@ = k@ / 3
PRINT #1, k@
m@ = 5
n@ = 2
m@ = m@ / n@
PRINT #1, m@

' BCD noise propagation from SINGLE sources
b@@ = 0.1
c@@ = 0.2
PRINT #1, b@@ + c@@

' big BCD prints through the 15-digit formatter
b@@ = 123456789012345678
PRINT #1, b@@

CLOSE #1
END
