' Differential battery 133: an element of a record's ARRAY FIELD that is a number or a fixed string,
' each subscript measured from the field's own lower bound. Genuine PBC 3.50 lays v(1 TO 3) out as
' three elements starting at 1 and v(4) as five starting at 0; the neighbours either side show an
' element landing one off. A scalar element was refused outright before.
TYPE Cell
  Id AS INTEGER
END TYPE
TYPE Rec
  h AS INTEGER
  v(1 TO 3) AS INTEGER
  w(4) AS LONG
  nm(2 TO 3) AS STRING * 3
  g(1 TO 2, 0 TO 2) AS BYTE
  c(1 TO 2) AS Cell
  t AS INTEGER
END TYPE
DIM r AS Rec, rs(1) AS Rec
OPEN "RESULT.TXT" FOR OUTPUT AS #1
r.v(1) = 7: r.v(3) = 5
r.w(0) = 100000: r.w(4) = -4
r.nm(2) = "abcdef": r.nm(3) = "z"
FOR i% = 1 TO 2
  FOR j% = 0 TO 2
    r.g(i%, j%) = i% * 10 + j%
  NEXT
NEXT
r.c(2).Id = 42
PRINT #1, r.h; r.t; r.v(1); r.v(2); r.v(3); r.w(0); r.w(4); LEN(r)
PRINT #1, "["; r.nm(2); "]["; r.nm(3); "]"
PRINT #1, r.g(1, 0); r.g(2, 0); r.g(1, 2); r.g(2, 2); r.c(1).Id; r.c(2).Id
rs(1) = r
rs(1).v(2) = rs(1).v(1) + rs(1).v(3)
k% = 3
PRINT #1, rs(1).v(2); rs(1).nm(k%); rs(1).g(2, 1); r.v(2)
CLOSE #1
