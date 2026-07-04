' @title: Discriminated unions + IS pattern tests
' @desc:  UNION with CASE members lowers to a tagged TYPE with overlapping payload views; s = Case(args) stores tag + fields; IF s IS Case v THEN binds a payload copy.
UNION Shape
  CASE Circle
    Radius AS SINGLE
  CASE Rect
    W AS INTEGER
    H AS INTEGER
  CASE Dot
END UNION
DIM s AS Shape
s = Rect(3, 4)
IF s IS Circle c THEN PRINT "circle"; c.Radius
IF s IS Rect r THEN PRINT "rect"; r.W * r.H
IF s IS Dot THEN PRINT "dot"
