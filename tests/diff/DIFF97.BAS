' Differential battery 97: O9 concat-chain dead-temp reuse.
' In a left-associative concat chain a$ + b$ + c$ = (a$ + b$) + c$, the inner concat produces a
' fresh, dead, topmost temp, so the next barrier-free operand is appended to it in place (reusing
' rt_strcatlit / rt_strcatvar) instead of allocating a new StrCat result per node. The operands are
' never mutated (raw handles are only read), so the string value is identical - byte-identical to
' genuine PBC 3.50 under pb35 and pb36.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

a$ = "AA"
b$ = "bb"
c$ = "CC"
d$ = "dd"
e$ = "EE"

' variable chain
s$ = a$ + b$ + c$
PRINT #1, "v3 "; s$

' longer variable chain
t$ = a$ + b$ + c$ + d$ + e$
PRINT #1, "v5 "; t$

' literal tails interleaved with variable tails
u$ = a$ + "-" + b$ + "-" + c$
PRINT #1, "lit "; u$

' an operand reused as the tail - must NOT be mutated by the in-place append
w$ = a$ + b$ + a$
PRINT #1, "reuse "; w$; " a="; a$

' the operands survive unchanged for later use
PRINT #1, "src "; a$; b$; c$; d$; e$

' right-leaning association is not chain-optimized but must still be correct
r$ = a$ + (b$ + c$)
PRINT #1, "right "; r$

' empty-literal tail falls back to the normal path
x$ = a$ + b$ + ""
PRINT #1, "empty "; x$

' chain built in a loop (each iteration a fresh chain)
FOR i% = 1 TO 4
  y$ = a$ + b$ + c$
NEXT i%
PRINT #1, "loop "; y$

CLOSE #1
END
