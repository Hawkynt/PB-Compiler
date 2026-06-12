' \ and MOD by powers of two: truncation-toward-zero + dividend-sign MOD
' must survive the pb36 shift/mask strength reduction bit-exactly
DECLARE FUNCTION Bump%(x%)
OPEN "RESULT.TXT" FOR OUTPUT AS #1
A% = -29
PRINT #1, A% \ 8; A% MOD 8; A% \ 2; A% MOD 2; A% \ 1; A% MOD 1
B% = 29
PRINT #1, B% \ 8; B% MOD 8; B% \ 256; B% MOD 256
C% = -32768
PRINT #1, C% \ 2; C% MOD 2; C% \ 16384; C% MOD 16384
M% = -32
PRINT #1, M% \ 8; M% MOD 8; 0 \ 8; 0 MOD 8
D& = -100000
PRINT #1, D& \ 4; D& MOD 4; D& \ 256; D& MOD 256
E& = -2147483647 - 1
PRINT #1, E& \ 2; E& MOD 2
F??? = 4000000000
PRINT #1, F??? \ 4; F??? MOD 4
F??? = 4000000001
PRINT #1, F??? \ 8; F??? MOD 8
G% = 0
PRINT #1, Bump%(G%) \ 4; G%
PRINT #1, Bump%(G%) MOD 4; G%
CLOSE #1
END
FUNCTION Bump%(x%)
  x% = x% + 100
  Bump% = x%
END FUNCTION
