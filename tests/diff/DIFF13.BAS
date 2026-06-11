' Differential battery 13: vendor-corpus string surface - INSTR ANY, VERIFY,
' EXTRACT$, TALLY, REPLACE, variadic CHR$, BIT statements, dotted variables,
' CASE relops without IS, spaced relations.
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' INSTR with ANY (member set) and plain
a$ = "12-34/56"
PRINT #1, INSTR(a$, ANY "-/")
PRINT #1, INSTR(4, a$, ANY "-/")
PRINT #1, INSTR(a$, "34")
PRINT #1, INSTR(a$, ANY "xyz")

' VERIFY: first char NOT in the set
PRINT #1, VERIFY("123A45", "0123456789")
PRINT #1, VERIFY("12345", "0123456789")
PRINT #1, VERIFY(3, "12 45", "0123456789")

' EXTRACT$ plain and ANY
PRINT #1, EXTRACT$("name=value", "=")
PRINT #1, EXTRACT$("name", "=")
PRINT #1, EXTRACT$("abc,def/ghi", ANY ",/")

' TALLY substring and set
PRINT #1, TALLY("the cat and the hat", "the")
PRINT #1, TALLY("the cat and the hat", ANY "th")
PRINT #1, TALLY("aaaa", "aa")

' REPLACE all occurrences
t$ = "one-two-three"
REPLACE "-" WITH "+" IN t$
PRINT #1, t$
REPLACE "ee" WITH "EE" IN t$
PRINT #1, t$

' variadic CHR$
PRINT #1, CHR$(65, 66, 67, 49, 50)

' BIT SET / RESET / TOGGLE on INTEGER and LONG
w% = 0
BIT SET w%, 0
BIT SET w%, 14
PRINT #1, w%
BIT RESET w%, 0
PRINT #1, w%
BIT TOGGLE w%, 3
PRINT #1, w%
l& = 0
BIT SET l&, 30
PRINT #1, l&
BIT TOGGLE l&, 30
PRINT #1, l&
b% = 8
PRINT #1, BIT(b%, 3)
PRINT #1, BIT(b%, 2)

' dotted variable names without a TYPE
Max.X = 319
Max.Y = 199
PRINT #1, Max.X + Max.Y

' CASE with bare relops; spaced ">="
x% = 34
SELECT CASE x%
  CASE = 34
    PRINT #1, "eq34"
  CASE > 40
    PRINT #1, "gt40"
END SELECT
IF x% > = 34 THEN PRINT #1, "ge ok"
IF x% < > 35 THEN PRINT #1, "ne ok"

' juxtaposed PRINT items
PRINT #1, "A" x% "B"

CLOSE #1
END
