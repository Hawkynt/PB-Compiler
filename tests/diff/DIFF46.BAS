' Differential battery 46: O8 +/-1 INC/DEC peephole keeps the overflow trap.
' Folding +/-1 into INC AX / DEC AX (one byte) must still set OF, so under
' $ERROR OVERFLOW ON the checked increment/decrement raises error 6 exactly
' where ADD/SUB AX,1 would; in-range results compute identically.
$ERROR OVERFLOW ON
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' ---- INTEGER + 1 at the positive limit overflows ---------------------------
ON ERROR GOTO Trap1
e% = 0
x% = 32767
y% = x% + 1
After1:
PRINT #1, e%; y%

' ---- INTEGER - 1 at the negative limit overflows ---------------------------
ON ERROR GOTO Trap2
e% = 0
x% = -32768
y% = x% - 1
After2:
PRINT #1, e%; y%

' ---- in-range increment and decrement --------------------------------------
ON ERROR GOTO Trap3
e% = 0
x% = 100
y% = x% + 1
z% = x% - 1
After3:
PRINT #1, e%; y%; z%

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
