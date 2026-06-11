' integer arithmetic promotes to floating in expression context (PB lineage)
OPEN "RESULT.TXT" FOR OUTPUT AS #1
PRINT #1, 32767 + 1; 20000 + 20000; 2000 * 2000; -2000 * 3000
A% = 30000
B% = 30000
PRINT #1, A% + B%; A% * B%; -A% - B%
C% = A% + B%
PRINT #1, C%
A2% = 32767
B2% = 32767
L& = A2% * B2%
PRINT #1, L&
D& = 2147483647
PRINT #1, D& + 1; D& * 2
E% = 1000
PRINT #1, E% * E% \ 7; E% * E% MOD 7
N% = -32768
PRINT #1, -N%
W1?? = 60000
PRINT #1, W1?? + W1??; W1?? * 2
CLOSE #1
END
