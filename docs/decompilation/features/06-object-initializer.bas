' @title: Object initializer
' @desc:  `DIM p = NEW Udt { .f = v }` lowers to a DIM plus one field assignment per listed field.
TYPE Point
  X AS INTEGER
  Y AS INTEGER
END TYPE
DIM P = NEW Point { .X = 3, .Y = 4 }
PRINT P.X; P.Y
