' Differential battery 83: O9 string self-append reuse.
' Under pb36 $OPTIMIZE SPEED a self-append `s$ = s$ + rhs` (rhs a string literal or bare
' variable) passes s$'s handle straight to StrCat and stores the result, skipping the
' redundant StrDup of s$ and the StrAssign free. The resulting string value is identical, so
' output is byte-identical to pb35 and genuine PBC 3.50 - even with the heap churn changed.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' string-building loop: self-append a literal
s$ = ""
FOR i% = 1 TO 10
  s$ = s$ + "ab"
NEXT i%
PRINT #1, "build"; s$; LEN(s$)

' self-append a variable
t$ = ""
d$ = "-"
FOR i% = 1 TO 5
  t$ = t$ + d$
NEXT i%
PRINT #1, "var"; t$

' self-double: s$ = s$ + s$ (the rhs copy must survive freeing the left)
u$ = "x"
FOR i% = 1 TO 4
  u$ = u$ + u$
NEXT i%
PRINT #1, "dbl"; u$; LEN(u$)

' self-append interleaved with another string allocation each iteration
v$ = "A"
FOR i% = 1 TO 6
  w$ = "[" + v$ + "]"
  v$ = v$ + "B"
NEXT i%
PRINT #1, "intlv"; v$; " "; w$

CLOSE #1
END
