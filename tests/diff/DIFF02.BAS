' Differential battery 2: string intrinsics vs genuine PBC 3.5.
DIM a AS STRING, b AS STRING
OPEN "RESULT.TXT" FOR OUTPUT AS #1
a = "HELLO"
b = a + ", " + "WORLD"
PRINT #1, b
PRINT #1, LEN(b)
PRINT #1, LEFT$(b, 5)
PRINT #1, RIGHT$(b, 5)
PRINT #1, MID$(b, 8, 5)
PRINT #1, UCASE$("hello")
PRINT #1, LCASE$("HELLO")
PRINT #1, INSTR(b, "WORLD")
PRINT #1, INSTR(2, "ababab", "ab")
PRINT #1, CHR$(65)
PRINT #1, ASC("A")
PRINT #1, STRING$(5, "x")
PRINT #1, STRING$(3, 42)
PRINT #1, SPACE$(3) + "|"
PRINT #1, LTRIM$("   x")
PRINT #1, RTRIM$("x   ") + "|"
PRINT #1, REPEAT$(3, "ab")
MID$(b, 1, 5) = "HOWDY"
PRINT #1, b
PRINT #1, LEN("")
PRINT #1, INSTR("abc", "z")
PRINT #1, MID$("abc", 5, 2) + "|"
PRINT #1, LEFT$("abc", 0) + "|"
PRINT #1, RIGHT$("abc", 99)
CLOSE #1
END
