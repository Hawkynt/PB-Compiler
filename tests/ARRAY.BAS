' Arrays: static multi-dim, dynamic REDIM/UBOUND/LBOUND, arrays of UDT
' with member access and fixed-string fields, string arrays.
TYPE Point
  x AS INTEGER
  y AS LONG
  tag AS STRING * 4
END TYPE

DIM grid(1 TO 3, 1 TO 4) AS INTEGER
DIM pts(2) AS Point
DIM i AS INTEGER, j AS INTEGER

FOR i = 1 TO 3
  FOR j = 1 TO 4
    grid(i, j) = i * 10 + j
  NEXT j
NEXT i
PRINT grid(1, 1); grid(2, 3); grid(3, 4)

REDIM dyn(5 TO 9) AS LONG
PRINT LBOUND(dyn); UBOUND(dyn)
FOR i = 5 TO 9
  dyn(i) = i * 1000&
NEXT i
PRINT dyn(5); dyn(9)

pts(1).x = 7
pts(1).y = 123456
pts(1).tag = "AB"
pts(2) = pts(1)
PRINT pts(2).x; pts(2).y
PRINT "["; pts(2).tag; "]"

REDIM dyn(1 TO 3) AS LONG
PRINT UBOUND(dyn)
dyn(2) = 42
PRINT dyn(2)

DIM names(1 TO 3) AS STRING
names(2) = "HI " + "THERE"
PRINT names(2); LEN(names(2))
END
