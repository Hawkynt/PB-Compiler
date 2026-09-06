' Differential battery 122: OPTION BASE also governs omitted lower bounds of dynamic arrays.
' The first array proves the setting is lexical: its DIM appears before OPTION BASE 1 and therefore
' starts at zero, while its later REDIM appears after the directive and starts at one. The second
' array proves a dynamic multidimensional DIM and REDIM PRESERVE both inherit base 1, and the third
' proves the same rule when REDIM introduces the array itself.
DIM early%(2)
OPTION BASE 1
OPEN "RESULT.TXT" FOR OUTPUT AS #1

PRINT #1, LBOUND(early%); UBOUND(early%)
REDIM early%(3)
PRINT #1, LBOUND(early%); UBOUND(early%)

DIM a%(4, 5)
a%(4, 5) = 45
PRINT #1, LBOUND(a%, 1); UBOUND(a%, 1); LBOUND(a%, 2); UBOUND(a%, 2)
REDIM PRESERVE a%(4, 6)
PRINT #1, LBOUND(a%, 1); UBOUND(a%, 1); LBOUND(a%, 2); UBOUND(a%, 2); a%(4, 5)

REDIM b%(3)
PRINT #1, LBOUND(b%); UBOUND(b%)

CLOSE #1
END
