' Differential battery 122: OPTION BASE also governs omitted lower bounds of dynamic arrays.
' The static DIM case was already covered by binder tests; this battery keeps the declaration dynamic
' by REDIMming it later, then proves both the initial DIM and REDIM PRESERVE use base 1. A separate
' REDIM-only array proves the same rule when REDIM introduces the array itself.
OPTION BASE 1
OPEN "RESULT.TXT" FOR OUTPUT AS #1

DIM a%(4, 5)
a%(4, 5) = 45
PRINT #1, LBOUND(a%, 1); UBOUND(a%, 1); LBOUND(a%, 2); UBOUND(a%, 2)
REDIM PRESERVE a%(4, 6)
PRINT #1, LBOUND(a%, 1); UBOUND(a%, 1); LBOUND(a%, 2); UBOUND(a%, 2); a%(4, 5)

REDIM b%(3)
PRINT #1, LBOUND(b%); UBOUND(b%)

CLOSE #1
END
