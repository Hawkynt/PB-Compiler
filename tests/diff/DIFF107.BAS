' Differential battery 107: O9 dead-temp reuse extended to substring-function lefts.
' A substring constructor (LEFT$/RIGHT$/MID$) produces a fresh, dead, topmost heap temp - just
' like a concat - so a following barrier-free operand (literal or variable) is appended to it IN
' PLACE (rt_strcatlit / rt_strcatvar) instead of allocating a new StrCat result. A bare variable
' or array element is live storage and is NOT reused. The runtime still verifies topmost at run
' time and falls back to a copy, so the result is byte-identical to genuine PBC 3.50 under pb35/pb36.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

x$ = "hello world"
v$ = "!!"

PRINT #1, LEFT$(x$, 5) + "-lit"          ' LEFT$ temp + literal -> in place
PRINT #1, RIGHT$(x$, 5) + v$             ' RIGHT$ temp + variable -> in place
PRINT #1, MID$(x$, 4, 3) + "X" + v$      ' chain off a MID$ temp
PRINT #1, x$ + v$                         ' bare-variable left: NOT reused (still correct)
PRINT #1, LEFT$(x$, 3) + MID$(x$, 7, 5)   ' temp + a temp right (right operand allocates) - copy path

CLOSE #1
END
