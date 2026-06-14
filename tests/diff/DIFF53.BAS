' Differential battery 53: O4 reciprocal-multiply division by a constant.
' Under $OPTIMIZE SPEED a signed INTEGER divide/MOD by a positive non-power-of-
' two constant becomes a verified MUL+shift instead of IDIV; the magic is
' brute-force-checked against every int16 at compile time, so every quotient and
' remainder must equal genuine PBC's IDIV - across negatives, zero and limits.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' small non-power-of-two divisors over signed dividends (counter varies)
FOR x% = -25 TO 25 STEP 5
  PRINT #1, x% \ 10; x% MOD 10; x% \ 3; x% MOD 3; x% \ 7; x% MOD 7
NEXT x%

' a power-of-two divisor still uses the shift path (sanity)
FOR p% = -16 TO 16 STEP 8
  PRINT #1, p% \ 8; p% MOD 8
NEXT p%

' larger divisors and dividends near the limits
FOR y% = -32000 TO 32000 STEP 8000
  PRINT #1, y% \ 100; y% MOD 100; y% \ 1000; y% \ 12345; y% MOD 1000
NEXT y%

' boundary dividends
b1% = -32768
b2% = 32767
PRINT #1, b1% \ 3; b1% MOD 3; b2% \ 7; b2% MOD 7; b1% \ 10; b2% \ 100
PRINT #1, b1% \ 6; b2% MOD 9; b1% \ 255; b2% \ 255

CLOSE #1
END
