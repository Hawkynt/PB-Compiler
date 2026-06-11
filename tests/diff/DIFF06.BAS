' Differential battery 6: '&' string concatenation (PB 3.5) vs '+',
' radix/concat lexing ambiguity, and QUAD basics.
OPEN "RESULT.TXT" FOR OUTPUT AS #1
a$ = "Hello"
b$ = "World"
c$ = a$ & ", " & b$ & "!"
PRINT #1, c$
PRINT #1, a$ + "+" & b$
l& = 7
c$ = "x" & STR$(l&)        ' long-suffixed variable next to the concat operator
PRINT #1, c$
q&& = 4000000000
q&& = q&& + q&&
PRINT #1, q&&
q&& = -9223372036854775807
PRINT #1, q&&
q&& = 123456789
PRINT #1, q&& * 1000000
PRINT #1, STR$(q&&)
PRINT #1, q&& > 123456788
PRINT #1, q&& = 123456789
d??? = 4000000000???
PRINT #1, STR$(d???)
CLOSE #1
END
