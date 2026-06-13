' Differential battery 45: O8 int16 add/sub immediate keeps the overflow trap.
' Folding a constant into ADD AX,imm / SUB AX,imm must still set OF, so under
' $ERROR OVERFLOW ON the checked add/sub raises error 6 exactly where the
' genuine register-operand form would; in-range results compute identically.
$ERROR OVERFLOW ON
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' ---- INTEGER + const overflow ----------------------------------------------
ON ERROR GOTO Trap1
e% = 0
x% = 30000
y% = x% + 3000
After1:
PRINT #1, e%; y%

' ---- INTEGER + const in range ----------------------------------------------
ON ERROR GOTO Trap2
e% = 0
x% = 30000
y% = x% + 700
After2:
PRINT #1, e%; y%

' ---- INTEGER - const overflow (toward the negative limit) -------------------
ON ERROR GOTO Trap3
e% = 0
x% = -30000
y% = x% - 3000
After3:
PRINT #1, e%; y%

' ---- INTEGER - const in range ----------------------------------------------
ON ERROR GOTO Trap4
e% = 0
x% = -30000
y% = x% - 700
After4:
PRINT #1, e%; y%

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
