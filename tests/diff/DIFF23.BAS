' Differential battery 23: whole-TYPE copies and comparisons over odd and
' even sizes - pins the pb36 word/dword block-move widening (tail bytes!).
TYPE Odd7
  a AS INTEGER
  b AS LONG
  c AS BYTE
END TYPE

TYPE Even12
  x AS LONG
  y AS LONG
  z AS LONG
END TYPE

DIM o1 AS Odd7
DIM o2 AS Odd7
DIM e1 AS Even12
DIM e2 AS Even12

OPEN "RESULT.TXT" FOR OUTPUT AS #1
o1.a = -123
o1.b = 987654
o1.c = 250
o2 = o1
PRINT #1, o2.a; o2.b; o2.c
PRINT #1, o1 = o2
o2.c = 1
PRINT #1, o1 = o2
PRINT #1, o1 <> o2

e1.x = 1
e1.y = 2000000
e1.z = -3
e2 = e1
PRINT #1, e2.x; e2.y; e2.z
PRINT #1, e1 = e2
e2.y = 0
PRINT #1, e1 <> e2
CLOSE #1
END
