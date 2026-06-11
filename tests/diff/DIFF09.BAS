' Differential battery 9: data pointers (PB 3.2/3.5) - PTR declarations,
' @ dereference as lvalue/rvalue, indexed @p[i], pointer to UDT fields.
TYPE Pt
  X AS INTEGER
  Y AS INTEGER
END TYPE

DIM p AS INTEGER PTR
DIM q AS Pt PTR
DIM a%(1 TO 5)
DIM v AS Pt

OPEN "RESULT.TXT" FOR OUTPUT AS #1
x% = 11
p = VARPTR32(x%)
PRINT #1, @p
@p = 42
PRINT #1, x%

FOR i% = 1 TO 5
  a%(i%) = i% * 10
NEXT i%
p = VARPTR32(a%(1))
PRINT #1, @p[0]
PRINT #1, @p[2]
@p[4] = 99
PRINT #1, a%(5)

v.X = 7
v.Y = -3
q = VARPTR32(v)
PRINT #1, @q.X
PRINT #1, @q.Y
@q.Y = 33
PRINT #1, v.Y
CLOSE #1
END
