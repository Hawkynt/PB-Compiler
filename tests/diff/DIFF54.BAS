' Differential battery 54: string operations vs genuine PBC 3.5.
' LEFT$/RIGHT$/MID$/INSTR/LEN/UCASE$/LCASE$/LTRIM$/RTRIM$/STRING$/SPACE$ plus
' VAL/STR$/HEX$/OCT$/CHR$/ASC and comparisons - every result must be byte-exact.
OPEN "RESULT.TXT" FOR OUTPUT AS #1

a$ = "Hello, World!"
PRINT #1, LEN(a$); LEFT$(a$, 5); RIGHT$(a$, 6); MID$(a$, 8, 5)
PRINT #1, MID$(a$, 8); INSTR(a$, "o"); INSTR(5, a$, "o"); INSTR(a$, "z")
PRINT #1, UCASE$(a$); LCASE$(a$)

b$ = "   padded   "
PRINT #1, "["; LTRIM$(b$); "]"; "["; RTRIM$(b$); "]"
PRINT #1, LEN(STRING$(5, "*")); STRING$(5, 65); SPACE$(3); "|"

PRINT #1, VAL("123"); VAL("  -45.5 "); VAL("3.14abc"); VAL("&HFF")
PRINT #1, STR$(42); STR$(-7); STR$(3.5)
PRINT #1, HEX$(255); HEX$(&H1234); OCT$(8); CHR$(65); ASC("A"); ASC("abc")

' comparisons and concatenation
PRINT #1, ("abc" = "abc"); ("abc" < "abd"); ("ABC" < "abc"); ("a" > "")
c$ = "foo" + "bar"
PRINT #1, c$; LEN(c$)

' MID$ assignment and repeated build
d$ = "ABCDEF"
MID$(d$, 2, 3) = "xyz"
PRINT #1, d$

CLOSE #1
END
