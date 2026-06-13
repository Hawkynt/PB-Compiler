' Differential battery 41: $ERROR OVERFLOW ON over multiply (O4 soundness).
' A strength-reduced shift chain cannot raise the genuine IMUL's error 6, so
' the pb36 multiply must keep its overflow trap under checking: every overflow
' below raises error 6 exactly where genuine PBC's IMUL would, and the
' in-range products compute identically.
$ERROR OVERFLOW ON
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' ---- INTEGER * 2 overflow raises error 6 (the bug: a SHL would wrap silently)
ON ERROR GOTO Trap1
e% = 0
x% = 30000
y% = x% * 2
After1:
PRINT #1, e%; y%

' ---- INTEGER * 2 in range computes and does not trap ------------------------
ON ERROR GOTO Trap2
e% = 0
x% = 10000
y% = x% * 2
After2:
PRINT #1, e%; y%

' ---- INTEGER * 4 overflow (power of two > 2) --------------------------------
ON ERROR GOTO Trap3
e% = 0
x% = 9000
y% = x% * 4
After3:
PRINT #1, e%; y%

' ---- INTEGER * 8 in range ---------------------------------------------------
ON ERROR GOTO Trap4
e% = 0
x% = 4000
y% = x% * 8
After4:
PRINT #1, e%; y%

' ---- INTEGER * 16 overflow (CL-shift territory) -----------------------------
ON ERROR GOTO Trap5
e% = 0
x% = 3000
y% = x% * 16
After5:
PRINT #1, e%; y%

' ---- LONG * 2 in range (faithful arithmetic, no trap) -----------------------
ON ERROR GOTO Trap6
e% = 0
a& = 1000000
b& = a& * 2
After6:
PRINT #1, e%; b&

CLOSE #1
END

Trap1:
e% = ERR
RESUME After1
Trap2:
e% = ERR
RESUME After2
Trap3:
e% = ERR
RESUME After3
Trap4:
e% = ERR
RESUME After4
Trap5:
e% = ERR
RESUME After5
Trap6:
e% = ERR
RESUME After6

