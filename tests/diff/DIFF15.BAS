' Differential battery 15: QUAD integer division, modulo, bitwise family and
' SHIFT/ROTATE on 8-byte cells (memory-based 4-word routines).
OPEN "RESULT.TXT" FOR OUTPUT AS #1

a&& = 1000000000007
b&& = 37
PRINT #1, a&& \ b&&
PRINT #1, a&& MOD b&&
PRINT #1, -a&& \ b&&
PRINT #1, -a&& MOD b&&
PRINT #1, a&& \ -b&&
PRINT #1, a&& MOD -b&&

c&& = 123456789012
d&& = 987654321
PRINT #1, c&& \ d&&
PRINT #1, c&& MOD d&&

x&& = 1099511627775
y&& = 76861433640456465
PRINT #1, x&& AND y&&
PRINT #1, x&& OR 256
PRINT #1, x&& XOR x&&
PRINT #1, NOT x&&
PRINT #1, x&& EQV x&&
PRINT #1, x&& IMP 0

s&& = 3
SHIFT LEFT s&&, 40
PRINT #1, s&&
SHIFT RIGHT s&&, 8
PRINT #1, s&&
ROTATE LEFT s&&, 4
PRINT #1, s&&
ROTATE RIGHT s&&, 4
PRINT #1, s&&

q&& = 7
PRINT #1, q&& \ 2
PRINT #1, q&& MOD 2
PRINT #1, 5000000000 \ 3
PRINT #1, 5000000000 MOD 3

CLOSE #1
END
