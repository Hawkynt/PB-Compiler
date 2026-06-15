' Differential battery 61: DWORD arithmetic and comparison vs signed operands.
' A DWORD operand keeps the result unsigned (it has no 32-bit signed equivalent),
' so DWORD + signed stays DWORD and DWORD comparisons run unsigned, while a WORD
' mixed with a signed type widens to signed LONG.
OPEN "RESULT.TXT" FOR OUTPUT AS #1
d??? = 4000000000
g& = 5
PRINT #1, d??? + g&             ' DWORD + LONG -> 4000000005 (not signed -294967291)
PRINT #1, d??? + 5              ' DWORD + literal -> 4000000005
e??? = d??? + 5
PRINT #1, e???
f??? = d??? * 1
PRINT #1, f???
IF d??? > 100 THEN PRINT #1, "GT" ELSE PRINT #1, "LE"   ' unsigned: GT
IF d??? > g& THEN PRINT #1, "GT" ELSE PRINT #1, "LE"    ' unsigned: GT
l& = -1
IF d??? > l& THEN PRINT #1, "GT" ELSE PRINT #1, "LE"    ' -1 reads as huge unsigned: LE
PRINT #1, d??? AND 255          ' bitwise stays DWORD
w?? = 50000
i% = -1
IF w?? > i% THEN PRINT #1, "GT" ELSE PRINT #1, "LE"     ' WORD widens to signed LONG: GT
IF w?? > 100 THEN PRINT #1, "GT" ELSE PRINT #1, "LE"    ' GT
CLOSE #1
END
