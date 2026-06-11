' Differential battery 14: ARRAY SORT/SCAN, LSET/RSET, USING$ (incl. grouping),
' WRITE #, ITERATE, MIN/MAX, EXIT FAR.
DECLARE SUB Noisy(n%)

DIM a$(1:8)
a$(1) = "pear"
a$(2) = "Apple"
a$(3) = "fig"
a$(4) = "date"
a$(5) = "cherry"
a$(6) = "plum"
a$(7) = "kiwi"
a$(8) = "apricot"

OPEN "RESULT.TXT" FOR OUTPUT AS #1

ARRAY SORT a$(1) FOR 8
FOR i% = 1 TO 8
  PRINT #1, a$(i%)
NEXT i%

ARRAY SORT a$(2) FOR 4, DESCEND
FOR i% = 1 TO 8
  PRINT #1, a$(i%)
NEXT i%

ARRAY SCAN a$(1) FOR 8, = "fig", TO f%
PRINT #1, f%
ARRAY SCAN a$(1) FOR 8, = "zzz", TO f%
PRINT #1, f%
ARRAY SCAN a$(3) FOR 6, > "kiwi", TO f%
PRINT #1, f%

' FROM/TO character range scan
DIM p$(1:3)
p$(1) = "AAAXX1"
p$(2) = "BBBYY2"
p$(3) = "CCCYY3"
ARRAY SCAN p$(1) FOR 3, FROM 4 TO 5, = "YY", TO f%
PRINT #1, f%

' LSET / RSET into dynamic and fixed strings
s$ = SPACE$(10)
LSET s$ = "abc"
PRINT #1, "["; s$; "]"
RSET s$ = "abc"
PRINT #1, "["; s$; "]"
DIM fs AS STRING * 8
LSET fs = "xy"
PRINT #1, "["; fs; "]"
RSET fs = "xy"
PRINT #1, "["; fs; "]"

' USING$ plain, fractional and grouped
PRINT #1, USING$("###", 5)
PRINT #1, USING$("##.##", 3.456)
PRINT #1, USING$("###,###,###", 1234567)
PRINT #1, USING$("x=## y", 42)
d??? = 1234567
PRINT #1, LTRIM$(USING$("###,###,###", d???))

' WRITE #
WRITE #1, 1, "two", 3.5

' ITERATE
t% = 0
FOR i% = 1 TO 10
  IF i% MOD 2 = 0 THEN ITERATE FOR
  t% = t% + i%
NEXT i%
PRINT #1, t%

' MIN / MAX variadic
PRINT #1, MIN(4, 5, 2, -1)
PRINT #1, MAX(4, 5, 2, -1)

' EXIT FAR unwinds nested calls
EXIT FAR AT Unwound
Noisy 3
PRINT #1, "not reached"
Unwound:
PRINT #1, "unwound"

CLOSE #1
END

SUB Noisy(n%)
  IF n% = 0 THEN
    EXIT FAR
  END IF
  Noisy n% - 1
  PRINT #1, "after"; n%
END SUB
