' String boundary and exceptional cases + PRINT comma zones.
DIM a AS STRING
a = "abc"
PRINT "["; LEFT$(a, 0); "]"
PRINT "["; MID$(a, 5, 2); "]"
PRINT "["; RIGHT$(a, 9); "]"
PRINT INSTR(a, "zz")
PRINT INSTR(2, "ababab", "ab")
PRINT LEN("")
IF "ab" < "abc" THEN PRINT "PREFIX-LT"
IF "abd" > "abc" THEN PRINT "BYTE-GT"
IF "" = "" THEN PRINT "EMPTY-EQ"
PRINT STR$(-7)
PRINT VAL("-12.5")
PRINT VAL("  42abc")
PRINT HEX$(255); " "; HEX$(255, 4)
PRINT BIN$(5); " "; OCT$(8)
PRINT REPEAT$(3, "ab")
PRINT CHR$(65) + CHR$(66)
a = ""
PRINT LEN(a + "x")
PRINT "A", "B"
PRINT 1, 2
END
