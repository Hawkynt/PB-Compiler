' @title: Static assertion + compile-time reflection
' @desc:  $ASSERT checks at compile time (no code); TYPEOF$/SIZEOF/FIELDCOUNT/FIELDNAME$/FIELDOFFSET/FIELDSIZE fold to literals at bind time.
TYPE Point
  X AS INTEGER
  Y AS LONG
END TYPE
$ASSERT SIZEOF(Point) = 6, "Point layout drifted"
DIM p AS Point
PRINT SIZEOF(Point)
PRINT TYPEOF$(p)
PRINT FIELDCOUNT(Point)
PRINT FIELDNAME$(Point, 2); FIELDOFFSET(Point, Y); FIELDSIZE(Point, Y)
