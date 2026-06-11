' Differential battery 5: type-suffix system (? ?? ??? && @) and DEFtype.
DEFQUD Q
OPEN "RESULT.TXT" FOR OUTPUT AS #1
b? = 200
PRINT #1, b?
b? = b? + 100              ' BYTE wraps silently (no $ERROR NUMERIC)
PRINT #1, b?
w?? = 40000
PRINT #1, w??
w?? = w?? + 30000          ' WORD arithmetic promotes to LONG
PRINT #1, w??
d??? = 3000000000
PRINT #1, d???
i% = -12345
PRINT #1, i%
l& = 2000000000
PRINT #1, l&
qv = 1234567890123
PRINT #1, qv               ' DEFQUD makes qv a QUAD
PRINT #1, 65535??
PRINT #1, 255?
PRINT #1, 4000000000???
CLOSE #1
END
