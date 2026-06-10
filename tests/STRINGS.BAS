' Dynamic strings and the core intrinsic set.
DIM a AS STRING, b AS STRING
a = "HELLO"
b = a + ", " + "WORLD"
PRINT b
PRINT LEN(b)
PRINT LEFT$(b, 5)
PRINT RIGHT$(b, 5)
PRINT MID$(b, 8, 5)
PRINT UCASE$("abc"); LCASE$("DEF")
PRINT INSTR(b, "WORLD")
PRINT CHR$(65); ASC("A")
PRINT STR$(42)
PRINT VAL("123")
PRINT STRING$(3, "x"); SPACE$(2); "|"
MID$(b, 1, 5) = "HOWDY"
PRINT b
END
