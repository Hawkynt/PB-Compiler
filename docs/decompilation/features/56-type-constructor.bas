' @title: TYPE constructor
' @desc:  A SUB named like the TYPE is its constructor; p = TypeName(args) calls it with p as BYREF THIS.
TYPE Point
  x AS LONG
  y AS LONG
  SUB Point(BYVAL px AS LONG, BYVAL py AS LONG)
    THIS.x = px
    THIS.y = py
  END SUB
END TYPE
DIM p AS Point
p = Point(3, 4)
PRINT p.x
PRINT p.y
