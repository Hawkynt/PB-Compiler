' Differential battery 56: array operations vs genuine PBC 3.5.
' DIM/REDIM/REDIM PRESERVE/ERASE, LBOUND/UBOUND, multi-dimensional indexing,
' physical column-major layout, OPTION BASE, and element arithmetic - every value must be byte-identical.
OPTION BASE 1
DIM a%(1 TO 10)
DIM g%(1 TO 3, 1 TO 4)
' declared without a lower bound, so OPTION BASE supplies it - the arrays above state theirs
' and are unaffected, which is the half of the rule worth pinning
DIM b%(4)
OPEN "RESULT.TXT" FOR OUTPUT AS #1
PRINT #1, LBOUND(b%); UBOUND(b%)

FOR i% = 1 TO 10
  a%(i%) = i% * i%
NEXT i%
PRINT #1, a%(1); a%(5); a%(10); LBOUND(a%); UBOUND(a%)

' two-dimensional fill and read-back
FOR r% = 1 TO 3
  FOR c% = 1 TO 4
    g%(r%, c%) = r% * 10 + c%
  NEXT c%
NEXT r%
PRINT #1, g%(1, 1); g%(2, 3); g%(3, 4); LBOUND(g%, 2); UBOUND(g%, 2)

' physical layout: PowerBASIC stores the first subscript fastest. INTEGERs are two bytes,
' so moving along dimension 1 advances 2 bytes and along dimension 2 advances 3*2 = 6.
gbase& = CLNG(VARPTR(g%(1, 1)))
PRINT #1, CLNG(VARPTR(g%(2, 1))) - gbase&; CLNG(VARPTR(g%(1, 2))) - gbase&

' dynamic array REDIM and REDIM PRESERVE
REDIM d%(1 TO 5)
FOR i% = 1 TO 5
  d%(i%) = i% + 100
NEXT i%
REDIM PRESERVE d%(1 TO 8)
d%(6) = 106
PRINT #1, d%(1); d%(5); d%(6); UBOUND(d%)

' multidimensional PRESERVE may grow the last (slowest) dimension while keeping the prefix intact
REDIM p%(1 TO 2, 10 TO 11)
p%(1, 10) = 11
p%(2, 10) = 12
p%(1, 11) = 21
p%(2, 11) = 22
REDIM PRESERVE p%(1 TO 2, 10 TO 13)
PRINT #1, p%(1, 10); p%(2, 10); p%(1, 11); p%(2, 11); p%(1, 12); p%(2, 13)

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
