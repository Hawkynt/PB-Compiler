' Differential battery 108: O9 dead-temp reuse for NON-left-leaning concats.
' (a$+b$) + (c$+d$) - the right operand is itself a fresh dead temp. Evaluating left-then-right
' would leave the right topmost, blocking in-place growth; when both operands are pure the optimizer
' evaluates the RIGHT first so the LEFT dead temp ends up topmost, appends the right's bytes in place
' (rt_strcatvar) and frees the buried right temp - matching what the plain StrCat would have produced.
' Byte-identical to genuine PBC 3.50 under pb35 and pb36.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

a$ = "alpha"
b$ = "beta"
c$ = "gamma"
d$ = "delta"

PRINT #1, (a$ + b$) + (c$ + d$)            ' balanced tree: right is a dead temp
PRINT #1, (a$ + "-") + (b$ + c$ + d$)      ' deeper right subtree
PRINT #1, (a$ + b$ + c$) + (d$ + a$)       ' both sides multi-operand chains
PRINT #1, a$ + (b$ + c$)                    ' right-leaning: left is a bare var (NOT reused), still correct

CLOSE #1
END
