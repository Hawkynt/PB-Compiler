' Differential battery 56: array operations vs genuine PBC 3.5.
' DIM/REDIM/REDIM PRESERVE/ERASE, LBOUND/UBOUND, multi-dimensional indexing,
' OPTION BASE, and element arithmetic - every value must be byte-identical.
DIM a%(1 TO 10)
DIM g%(1 TO 3, 1 TO 4)
OPEN "RESULT.TXT" FOR OUTPUT AS #1

FOR i% = 1 TO 10
  a%(i%) = i% * i%
NEXT i%
PRINT #1, a%(1); a%(5); a%(10); LBOUND(a%); UBOUND(a%)

' two-dimensional fill and read-back (row-major / column-major order)
FOR r% = 1 TO 3
  FOR c% = 1 TO 4
    g%(r%, c%) = r% * 10 + c%
  NEXT c%
NEXT r%
PRINT #1, g%(1, 1); g%(2, 3); g%(3, 4); LBOUND(g%, 2); UBOUND(g%, 2)

' dynamic array REDIM and REDIM PRESERVE
REDIM d%(1 TO 5)
FOR i% = 1 TO 5
  d%(i%) = i% + 100
NEXT i%
REDIM PRESERVE d%(1 TO 8)
d%(6) = 106
PRINT #1, d%(1); d%(5); d%(6); UBOUND(d%)

ERASE d%
REDIM d%(1 TO 3)
PRINT #1, d%(1); d%(2); UBOUND(d%)

' sum across an array
s& = 0
FOR i% = 1 TO 10
  s& = s& + a%(i%)
NEXT i%
PRINT #1, s&

CLOSE #1
END
